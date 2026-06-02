using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentAppServiceDeleteTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

public class DocumentAppService_Delete_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceDeleteTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IBlobContainer<PaperbaseDocumentContainer> _blobContainer;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly ICabinetRepository _cabinetRepository;

    public DocumentAppService_Delete_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _distributedEventBus = GetRequiredService<IDistributedEventBus>();
        _blobContainer = GetRequiredService<IBlobContainer<PaperbaseDocumentContainer>>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();

        // UploadAsync 的前置 fail-fast 检查（当前层至少有一个 DocumentType）—— 测试默认走"已配置"路径，
        // 让 cabinet / 文件校验 / 重复 / 回收站检查能跑到。fail-fast 走 GetCountAsync()（#241，非 GetListAsync）；
        // 专门测试"未配置 → NoDocumentTypesConfigured"在对应 fact 里把 count 覆盖为 0。
        _documentTypeRepository.GetCountAsync(Arg.Any<CancellationToken>()).Returns(1L);
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletes_Document_Without_Removing_Blob()
    {
        var doc = CreateDocument();
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        await _appService.DeleteAsync(doc.Id);

        await _blobContainer.DidNotReceive().DeleteAsync(doc.FileOrigin.BlobName, Arg.Any<CancellationToken>());
        await _documentRepository.Received(1).DeleteAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_Publishes_DocumentDeletedEto()
    {
        var doc = CreateDocument();
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        await _appService.DeleteAsync(doc.Id);

        await _distributedEventBus.Received(1).PublishAsync(
            Arg.Is<DocumentDeletedEto>(e =>
                e.DocumentId == doc.Id &&
                e.TenantId == doc.TenantId),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task RestoreAsync_Restores_Deleted_Document_And_Publishes_Event()
    {
        var doc = CreateDocument();
        doc.IsDeleted = true;
        doc.DeletionTime = DateTime.UtcNow;
        doc.DeleterId = Guid.NewGuid();

        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);

        await _appService.RestoreAsync(doc.Id);

        doc.IsDeleted.ShouldBeFalse();
        doc.DeletionTime.ShouldBeNull();
        doc.DeleterId.ShouldBeNull();
        await _documentRepository.Received(1).UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _distributedEventBus.Received(1).PublishAsync(
            Arg.Is<DocumentRestoredEto>(e =>
                e.DocumentId == doc.Id &&
                e.TenantId == doc.TenantId),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task UploadAsync_Throws_NoDocumentTypesConfigured_When_Current_Scope_Has_No_Types()
    {
        // 覆盖 fail-fast：删除 Host 启动期 seed 后，新部署 / 新租户首次上传必须先建至少一个 DocumentType。
        // 不做这个检查的话，文档会上传成功 → 分类候选集为空 → 永远卡 PendingReview。
        _documentTypeRepository.GetCountAsync(Arg.Any<CancellationToken>()).Returns(0L);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _appService.UploadAsync(CreateUploadInput([1, 2, 3]));
        });

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentType.NoneConfigured);
    }

    [Fact]
    public async Task UploadAsync_Throws_Duplicate_When_ContentHash_Belongs_To_Active_Document()
    {
        var existing = CreateDocumentWithContent([1, 2, 3]);
        _documentRepository.FindByContentHashAsync(
                existing.FileOrigin.ContentHash,
                Arg.Any<CancellationToken>())
            .Returns(existing);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _appService.UploadAsync(CreateUploadInput([1, 2, 3]));
        });

        exception.Code.ShouldBe(PaperbaseErrorCodes.Document.Duplicate);
    }

    [Fact]
    public async Task UploadAsync_Throws_RecycleBin_Error_When_ContentHash_Belongs_To_Deleted_Document()
    {
        var existing = CreateDocumentWithContent([1, 2, 3]);
        existing.IsDeleted = true;
        _documentRepository.FindByContentHashAsync(
                existing.FileOrigin.ContentHash,
                Arg.Any<CancellationToken>())
            .Returns(existing);

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
        {
            await _appService.UploadAsync(CreateUploadInput([1, 2, 3]));
        });

        exception.Code.ShouldBe(PaperbaseErrorCodes.Document.InRecycleBin);
        exception.Data["ExistingDocumentId"].ShouldBe(existing.Id);
    }

    [Fact]
    public async Task UploadAsync_Files_Document_Into_Cabinet_When_CabinetId_Is_Valid()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        _cabinetRepository.FindAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(cabinet);

        var input = CreateUploadInput([9, 8, 7]);
        input.CabinetId = cabinet.Id;

        await _appService.UploadAsync(input);

        // 上传时人工归属：Document 以传入的 CabinetId 落库（已校验当前层柜存在 + Cabinets 权限）。
        await _documentRepository.Received(1).InsertAsync(
            Arg.Is<Document>(d => d.CabinetId == cabinet.Id),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Throws_InvalidCabinetId_When_Cabinet_Not_In_Current_Layer()
    {
        // 不 setup FindAsync → mock 默认返回 null（柜不存在 / 跨层被 ambient tenant filter 滤掉）。
        // 经过 CheckPolicyAsync(Cabinets.Default)（测试环境 AlwaysAllow）后落到 fail-closed 拒绝。
        var input = CreateUploadInput([4, 5, 6]);
        input.CabinetId = Guid.NewGuid();

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(PaperbaseErrorCodes.Cabinet.InvalidId);
    }

    [Fact]
    public async Task UploadAsync_Throws_UnsupportedFileType_When_ContentType_Not_Allowed()
    {
        // #221：扩展名合法但 content-type 不在白名单 → fail-closed，不落 blob、不入队。
        var input = CreateUploadInput([1, 2, 3], fileName: "A.pdf", contentType: "application/zip");

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(PaperbaseErrorCodes.Document.UnsupportedFileType);
        await _documentRepository.DidNotReceive().InsertAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _blobContainer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_Throws_UnsupportedFileType_When_Extension_Not_Allowed()
    {
        // #221：content-type 合法但扩展名不在白名单（决定 blob 后缀 + DefaultTextExtractor dispatch）→ fail-closed。
        var input = CreateUploadInput([1, 2, 3], fileName: "A.exe", contentType: "application/pdf");

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(PaperbaseErrorCodes.Document.UnsupportedFileType);
    }

    [Fact]
    public async Task UploadAsync_Throws_FileTooLarge_When_Declared_ContentLength_Exceeds_Limit()
    {
        // #221：客户端声明的 ContentLength 超限 → 廉价快速拒绝（不读流）。
        var input = new UploadDocumentInput
        {
            File = new RemoteStreamContent(
                new MemoryStream([1, 2, 3]),
                "A.pdf",
                "application/pdf",
                readOnlyLength: DocumentConsts.MaxUploadFileBytes + 1,
                disposeStream: true)
        };

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await _appService.UploadAsync(input));

        exception.Code.ShouldBe(PaperbaseErrorCodes.Document.FileTooLarge);
    }

    [Fact]
    public async Task UploadAsync_Throws_FileTooLarge_When_Streamed_Bytes_Exceed_Limit_Despite_Underreported_Length()
    {
        // #221：声明的 ContentLength 少报（不可信），但流式拷贝按实际字节数施加的硬上限仍兜住——
        // 不依赖客户端声明，也不把超大 body 全量缓冲。临时下调 static 上限（finally 复原，类内串行执行）。
        var original = DocumentConsts.MaxUploadFileBytes;
        try
        {
            DocumentConsts.MaxUploadFileBytes = 4;
            var input = new UploadDocumentInput
            {
                File = new RemoteStreamContent(
                    new MemoryStream(new byte[10]),
                    "A.pdf",
                    "application/pdf",
                    readOnlyLength: 3, // 少报，绕过廉价 ContentLength 检查
                    disposeStream: true)
            };

            var exception = await Should.ThrowAsync<BusinessException>(async () =>
                await _appService.UploadAsync(input));

            exception.Code.ShouldBe(PaperbaseErrorCodes.Document.FileTooLarge);
        }
        finally
        {
            DocumentConsts.MaxUploadFileBytes = original;
        }
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }

    private static Document CreateDocumentWithContent(byte[] bytes)
    {
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

        return new Document(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: contentHash,
                fileSize: bytes.LongLength,
                originalFileName: "test.pdf"));
    }

    private static UploadDocumentInput CreateUploadInput(
        byte[] bytes, string fileName = "A.pdf", string contentType = "application/pdf")
    {
        return new UploadDocumentInput
        {
            File = new RemoteStreamContent(
                new MemoryStream(bytes),
                fileName,
                contentType,
                disposeStream: true)
        };
    }
}
