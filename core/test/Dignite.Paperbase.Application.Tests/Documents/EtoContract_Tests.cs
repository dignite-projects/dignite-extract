using System;
using System.Text.Json;
using Dignite.Paperbase.Abstractions.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 出口 ETO 契约测试（issue #188）。
/// <para>
/// 验证 #188 引入的 <c>init</c>-only / <c>required</c> 改造没有破坏 System.Text.Json round-trip——
/// ABP 内置 transactional outbox 把 ETO 序列化为 JSON 写入 <c>AbpEventOutbox.EventData</c>，
/// 后台 worker 读出来再反序列化分发给 handler。如果 round-trip 失败，整个出口契约失效。
/// </para>
/// <para>
/// 不是测 ABP outbox 本身（那是 framework 行为），而是测**我们的 ETO 形状**与 System.Text.Json
/// 的兼容性。<see cref="System.Text.Json"/> 在 .NET 5+ 支持 <c>init</c>-only setter；
/// <c>required</c> 关键字只影响编译期对象初始化器检查，不影响反序列化（反射可以 set）。
/// </para>
/// </summary>
public class EtoContract_Tests
{
    private static readonly DateTime SampleEventTime =
        new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DocumentUploadedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentUploadedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EventTime = SampleEventTime,
            FileName = "x.pdf",
            FileSize = 1024,
            ContentType = "application/pdf"
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentId.ShouldBe(eto.DocumentId);
        roundTrip.TenantId.ShouldBe(eto.TenantId);
        roundTrip.EventTime.ShouldBe(eto.EventTime);
        roundTrip.FileName.ShouldBe(eto.FileName);
        roundTrip.FileSize.ShouldBe(eto.FileSize);
        roundTrip.ContentType.ShouldBe(eto.ContentType);
        roundTrip.Version.ShouldBe("1.0");
    }

    [Fact]
    public void OCRCompletedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new OCRCompletedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = null,
            EventTime = SampleEventTime,
            UsedOcr = true
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.EventTime.ShouldBe(eto.EventTime);
        roundTrip.UsedOcr.ShouldBeTrue();
    }

    [Fact]
    public void DocumentClassifiedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentClassifiedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EventTime = SampleEventTime,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.93
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentTypeCode.ShouldBe("contract.general");
        roundTrip.ClassificationConfidence.ShouldBe(0.93);
        roundTrip.EventTime.ShouldBe(eto.EventTime);
    }

    [Fact]
    public void FieldsExtractedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new FieldsExtractedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EventTime = SampleEventTime,
            DocumentTypeCode = "contract.general",
            FieldCount = 3
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.FieldCount.ShouldBe(3);
        roundTrip.DocumentTypeCode.ShouldBe("contract.general");
    }

    [Fact]
    public void DocumentReadyEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentReadyEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = null,
            EventTime = SampleEventTime,
            DocumentTypeCode = "contract.general"
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentTypeCode.ShouldBe("contract.general");
        roundTrip.EventTime.ShouldBe(eto.EventTime);
    }

    [Fact]
    public void DocumentDeletedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentDeletedEto
        {
            DocumentId = Guid.NewGuid(),
            EventTime = SampleEventTime
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentId.ShouldBe(eto.DocumentId);
        roundTrip.Version.ShouldBe("1.0");
    }

    [Fact]
    public void DocumentPermanentlyDeletedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentPermanentlyDeletedEto
        {
            DocumentId = Guid.NewGuid(),
            EventTime = SampleEventTime
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentId.ShouldBe(eto.DocumentId);
        roundTrip.Version.ShouldBe("1.0");
    }

    [Fact]
    public void DocumentRestoredEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentRestoredEto
        {
            DocumentId = Guid.NewGuid(),
            EventTime = SampleEventTime
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentId.ShouldBe(eto.DocumentId);
    }

    [Fact]
    public void EventTime_Missing_From_Json_Throws_On_Deserialize()
    {
        // 验证 `required` 关键字在 System.Text.Json (.NET 7+) 反序列化时的 fail-fast 行为：
        // JSON 缺 EventTime 字段 → 抛 JsonException，下游 worker 不会拿到 default(DateTime) 的事件，
        // 触发 outbox retry 或 inbox dead-letter，比静默吞噬强得多。
        var jsonWithoutEventTime = """
            {
              "DocumentId": "00000000-0000-0000-0000-000000000001",
              "FileSize": 1024
            }
            """;

        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<DocumentUploadedEto>(jsonWithoutEventTime));
    }

    private static T RoundTrip<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value);
        var roundTrip = JsonSerializer.Deserialize<T>(json);
        roundTrip.ShouldNotBeNull();
        return roundTrip;
    }
}
