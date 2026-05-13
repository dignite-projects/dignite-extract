using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Contracts.Chat;
using Microsoft.Agents.AI;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Contracts.EntityFrameworkCore.Chat;

/// <summary>
/// Issue #149 — Codex adversarial review findings 1 &amp; 2: regression coverage for the
/// parameter-defaults bug on <see cref="ContractsSkill"/>'s <c>search</c> and
/// <c>aggregate</c> scripts. Both had optional-looking filter parameters declared
/// without <c>= null</c> defaults, which <c>AIFunctionFactory</c> exposes as
/// <em>required</em> in the generated JSON schema (the spec the model uses to validate
/// <c>run_skill_script</c> arguments). The intended UX is "user says 'contracts with
/// Acme'" → the model passes only <c>partyName</c>; pre-fix, every omitted filter would
/// have caused a schema-validation failure before the query ran.
///
/// <para>
/// The tests exercise the real <see cref="AgentSkillScript.RunAsync"/> dispatcher path
/// — the same path MAF uses inside <c>run_skill_script</c> — so parameter binding is
/// covered by the framework itself, not just by the C# method's defaults. They also
/// run against the live EF Core stack so the repository / authorization / tenant
/// resolution all participate end-to-end.
/// </para>
///
/// <para>
/// Arch review C2 collapsed the three earlier skills (search-contracts /
/// get-contract-detail / aggregate-contracts) into a single <see cref="ContractsSkill"/>
/// with three scripts. These regression tests therefore target the consolidated
/// skill's named scripts (<c>search</c> / <c>get-detail</c> / <c>aggregate</c>) rather
/// than three separate <c>invoke</c> scripts.
/// </para>
/// </summary>
public class ContractSkillScriptInvocation_Tests : ContractsEntityFrameworkCoreTestBase
{
    private readonly IServiceProvider _serviceProvider;

    public ContractSkillScriptInvocation_Tests()
    {
        _serviceProvider = GetRequiredService<IServiceProvider>();
    }

    [Fact]
    public async Task Search_Script_Accepts_PartyName_Only_Without_Throwing()
    {
        // Regression for Codex finding 1.
        var json = await InvokeScriptAsync(
            scriptName: "search",
            args: JsonDocument.Parse("""{"partyName":"AcmeCorp"}""").RootElement);

        json.GetProperty("documentIds").GetArrayLength().ShouldBe(0);
        json.GetProperty("contracts").GetArrayLength().ShouldBe(0);
        json.TryGetProperty("note", out var note).ShouldBeTrue();
        note.GetString().ShouldNotBeNull().ShouldContain("search_paperbase_documents");
    }

    [Fact]
    public async Task Search_Script_Accepts_ContractNumber_Only_Without_Throwing()
    {
        var json = await InvokeScriptAsync(
            scriptName: "search",
            args: JsonDocument.Parse("""{"contractNumber":"INV-0001"}""").RootElement);

        json.GetProperty("contracts").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Search_Script_Accepts_Empty_Args_Object_Without_Throwing()
    {
        // No filters at all — the contract is "all parameters optional", so this must
        // also complete cleanly. With the regression, MAF would have raised
        // "missing required argument" before our method body ran.
        var json = await InvokeScriptAsync(
            scriptName: "search",
            args: JsonDocument.Parse("{}").RootElement);

        json.GetProperty("contracts").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Aggregate_Script_Accepts_PartyName_Only_Without_Throwing()
    {
        // Regression for Codex finding 2: same parameter-defaults bug, aggregate script.
        var json = await InvokeScriptAsync(
            scriptName: "aggregate",
            args: JsonDocument.Parse("""{"partyName":"AcmeCorp"}""").RootElement);

        json.GetProperty("groupBy").GetString().ShouldBe("currency");
        json.GetProperty("buckets").GetArrayLength().ShouldBe(0);
        json.GetProperty("grandCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Aggregate_Script_Accepts_Status_Only_Without_Throwing()
    {
        var json = await InvokeScriptAsync(
            scriptName: "aggregate",
            args: JsonDocument.Parse("""{"status":"Active"}""").RootElement);

        json.GetProperty("grandCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task GetDetail_Script_Returns_NotFound_When_Document_Missing()
    {
        // get-detail has a single required `documentId` parameter — partial-args
        // failures aren't possible there, but the not-found-fallback path is still
        // worth pinning so the empty-result `note` keeps chaining to the vector tool.
        var unknownId = Guid.NewGuid();
        var json = await InvokeScriptAsync(
            scriptName: "get-detail",
            args: JsonDocument.Parse($$"""{"documentId":"{{unknownId}}"}""").RootElement);

        json.GetProperty("found").GetBoolean().ShouldBeFalse();
        json.GetProperty("documentId").GetGuid().ShouldBe(unknownId);
        json.TryGetProperty("note", out var note).ShouldBeTrue();
        note.GetString().ShouldNotBeNull().ShouldContain("search_paperbase_documents");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helper
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes one of <see cref="ContractsSkill"/>'s named scripts through MAF's
    /// <see cref="AgentSkillScript.RunAsync"/> entry point — same code path that
    /// <c>AgentSkillsProvider</c>'s <c>run_skill_script</c> tool exercises when MAF
    /// dispatches a model-issued skill call. Drives a fresh skill instance per call
    /// (mirrors production: the skill is <c>ITransientDependency</c> in chat scope).
    /// </summary>
    private async Task<JsonElement> InvokeScriptAsync(string scriptName, JsonElement args)
    {
        var skill = new ContractsSkill();
        var script = skill.Scripts!.Single(s => s.Name == scriptName);
        var raw = await WithUnitOfWorkAsync(async () =>
            await script.RunAsync(skill, args, _serviceProvider, CancellationToken.None));

        // MAF's AIFunctionFactory marshals string-returning methods through
        // System.Text.Json: the returned `Task<string>` surfaces as a
        // <c>JsonElement</c> whose <see cref="JsonValueKind.String"/> value is the raw
        // JSON text produced by our skill. Unwrap once to get the parsed object.
        return raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } json =>
                JsonDocument.Parse(json.GetString()!).RootElement,
            JsonElement json => json,
            string text => JsonDocument.Parse(text).RootElement,
            null => throw new InvalidOperationException("Skill script returned null."),
            _ => JsonDocument.Parse(JsonSerializer.Serialize(raw)).RootElement
        };
    }
}
