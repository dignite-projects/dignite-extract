using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Slugging;

/// <summary>
/// SlugSuggestionAppService 的降级 / 取消语义（issue #190 + Codex 对抗式评审 finding 2）。
/// IChatClient 用 NSubstitute 替代，无真实 LLM 调用；不验证 CancelAfter 计时本身（标准 .NET 行为），
/// 只验证「OperationCanceledException 如何分流」与「异常 / 坏输出一律降级为空 slug」这套自有逻辑。
/// </summary>
public class SlugSuggestionAppService_Tests
{
    private static SlugSuggestionAppService CreateService(IChatClient chatClient)
        => new(chatClient, NullLogger<SlugSuggestionAppService>.Instance);

    private static IChatClient ChatClientReturning(string responseText)
    {
        var fake = Substitute.For<IChatClient>();
        fake.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)])));
        return fake;
    }

    private static IChatClient ChatClientThrowing(Exception ex)
    {
        var fake = Substitute.For<IChatClient>();
        fake.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ChatResponse>(ex));
        return fake;
    }

    [Fact]
    public async Task Returns_sanitized_snake_case_slug_from_json()
    {
        var svc = CreateService(ChatClientReturning("{\"slug\": \"Contract Amount!\"}"));

        var result = await svc.SuggestAsync(new SuggestSlugInput { DisplayName = "合同金额" });

        // 服务端 sanitize：小写、非 [a-z0-9] 折叠为下划线、合并、去首尾。
        result.Slug.ShouldBe("contract_amount");
    }

    [Fact]
    public async Task Returns_empty_when_output_is_not_json()
    {
        var svc = CreateService(ChatClientReturning("sorry, I cannot help with that"));

        var result = await svc.SuggestAsync(new SuggestSlugInput { DisplayName = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns_empty_when_slug_key_missing()
    {
        var svc = CreateService(ChatClientReturning("{\"name\": \"contract_amount\"}"));

        var result = await svc.SuggestAsync(new SuggestSlugInput { DisplayName = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns_empty_when_slug_has_no_ascii_after_sanitize()
    {
        // LLM 未翻译，原样返回 CJK —— sanitize 后无合法字符 → 空 slug → 前端回退本地占位。
        var svc = CreateService(ChatClientReturning("{\"slug\": \"合同\"}"));

        var result = await svc.SuggestAsync(new SuggestSlugInput { DisplayName = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns_empty_when_llm_throws()
    {
        var svc = CreateService(ChatClientThrowing(new InvalidOperationException("provider down")));

        var result = await svc.SuggestAsync(new SuggestSlugInput { DisplayName = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns_empty_on_server_deadline_cancellation()
    {
        // 模拟服务端 deadline（CancelAfter）触发：调用方令牌未取消，但 LLM 调用抛 OperationCanceledException。
        // 应降级为空 slug，而非向上抛。
        var svc = CreateService(ChatClientThrowing(new OperationCanceledException()));

        var result = await svc.SuggestAsync(new SuggestSlugInput { DisplayName = "合同金额" });

        result.Slug.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Propagates_cancellation_when_caller_cancels()
    {
        // 客户端主动断开：调用方令牌已取消 + LLM 抛 OperationCanceledException → 原样向上抛（不当作 LLM 失败吞掉）。
        var svc = CreateService(ChatClientThrowing(new OperationCanceledException()));
        var canceled = new CancellationToken(canceled: true);

        await Should.ThrowAsync<OperationCanceledException>(
            () => svc.SuggestAsync(new SuggestSlugInput { DisplayName = "合同金额" }, canceled));
    }
}
