using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Contracts.EventHandlers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Contracts.Ai;

/// <summary>
/// Parallel audit to the core one in
/// <c>core/test/Dignite.Paperbase.Application.Tests/Ai/KeyedChatClientRegistration_Tests.cs</c>,
/// scoped to the contracts business module. Walks <c>Dignite.Paperbase.Contracts.Domain</c>
/// for <see cref="FromKeyedServicesAttribute"/> consumers on <see cref="IChatClient"/>
/// parameters and asserts every key is one the host actually registers.
///
/// <para>
/// New business modules that inject keyed AI clients should add their own parallel
/// audit test class — the convention is one per module's Domain (or Application) test
/// project, sharing the same <see cref="HostRegisteredKeys"/> snapshot list. This avoids
/// pulling every module assembly into Application.Tests via ProjectReference.
/// </para>
/// </summary>
public class ContractsKeyedChatClientRegistration_Tests
{
    /// <summary>
    /// Snapshot of <c>PaperbaseHostModule.ConfigureAI</c>'s <c>AddKeyedChatClient(...)</c>
    /// calls. KEEP IN SYNC with that method AND with the matching snapshot in
    /// <c>KeyedChatClientRegistration_Tests</c> (core test) — both must declare the same set.
    /// </summary>
    private static readonly HashSet<string> HostRegisteredKeys = new()
    {
        PaperbaseAIConsts.SummarizerChatClientKey,
        PaperbaseAIConsts.TitleGeneratorChatClientKey,
        PaperbaseAIConsts.StructuredChatClientKey,
    };

    private static readonly Assembly[] ProductionAssemblies =
    {
        typeof(ContractDocumentHandler).Assembly,   // Dignite.Paperbase.Contracts.Domain
    };

    [Fact]
    public void Every_Consumed_Key_In_Contracts_Module_Is_Registered_By_The_Host()
    {
        var consumers = FindKeyedConsumers().ToList();
        // Allow an empty result here — a business module is not required to consume
        // any keyed client. The assertion below is the meaningful one.

        var orphans = consumers
            .Where(c => !HostRegisteredKeys.Contains(c.Key))
            .ToList();

        orphans.ShouldBeEmpty(
            "Contracts module references keyed IChatClient keys NOT registered by " +
            "PaperbaseHostModule.ConfigureAI. Either add the AddKeyedChatClient call there, " +
            "fix the typo, or update this test's HostRegisteredKeys snapshot to match: " +
            string.Join("; ", orphans.Select(o => $"{o.TypeName}.{o.ParamName} = \"{o.Key}\"")));
    }

    private static IEnumerable<KeyedConsumer> FindKeyedConsumers()
    {
        foreach (var asm in ProductionAssemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                foreach (var ctor in type.GetConstructors())
                {
                    foreach (var param in ctor.GetParameters())
                    {
                        var attr = param.GetCustomAttribute<FromKeyedServicesAttribute>();
                        if (attr?.Key is string key && param.ParameterType == typeof(IChatClient))
                        {
                            yield return new KeyedConsumer(
                                TypeName: type.Name,
                                ParamName: param.Name ?? "<unnamed>",
                                Key: key);
                        }
                    }
                }
            }
        }
    }

    private sealed record KeyedConsumer(string TypeName, string ParamName, string Key);
}
