using System.Collections.Generic;
using System.Linq;
using Microsoft.Agents.AI;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Contracts.Chat;

/// <summary>
/// Issue #149 follow-up: guard the ABP DI wiring that exposes
/// <see cref="ContractsSkill"/> to <c>ChatAppService</c>. The skill relies on
/// <c>[ExposeServices(typeof(AgentSkill))]</c> + <c>ITransientDependency</c> on the
/// <see cref="AgentClassSkill{TSelf}"/> subclass so ABP auto-registers it as
/// <see cref="AgentSkill"/>. If a future refactor drops one of those attributes (or
/// switches the base class), this test fails loudly — without it, the application
/// would still compile and chat would still run; only contract questions would
/// silently degrade because the skill never reached the agent's <c>AgentSkillsProvider</c>.
/// </summary>
public class ContractSkillRegistration_Tests
    : ContractsApplicationTestBase<ContractsApplicationTestModule>
{
    [Fact]
    public void ContractsApplicationModule_Registers_ContractsSkill_As_AgentSkill()
    {
        var skills = GetRequiredService<IEnumerable<AgentSkill>>().ToList();

        skills.ShouldContain(s => s is ContractsSkill,
            "ContractsSkill must be auto-registered as AgentSkill via [ExposeServices(typeof(AgentSkill))] + ITransientDependency");
    }

    [Fact]
    public void ContractsSkill_Advertises_The_Expected_Skill_Name_And_Three_Scripts()
    {
        // Arch review C2: one contracts skill, three scripts. LLM advertises ~100 tokens
        // (the skill name + Frontmatter description); per-script docstrings live inside
        // the SKILL.md instructions that load_skill returns.
        var contracts = GetRequiredService<IEnumerable<AgentSkill>>()
            .OfType<ContractsSkill>()
            .ShouldHaveSingleItem();

        contracts.Frontmatter.Name.ShouldBe("contracts");

        var scriptNames = contracts.Scripts.ShouldNotBeNull()
            .Select(s => s.Name)
            .ToHashSet();
        scriptNames.ShouldBe(new HashSet<string> { "search", "get-detail", "aggregate" });
    }
}
