using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Ai;

/// <summary>
/// Static contract test guarding the <c>[FromKeyedServices(...)]</c> / <c>AddKeyedChatClient(...)</c>
/// symmetry between production code and host wiring.
///
/// <para>
/// Background: the <c>IChatClient</c> sweep that landed across commits
/// <c>15005c6</c> (title generator), <c>37eb625</c> (document title reuse), and <c>7a5ef93</c>
/// (structured-output consolidation) discovered that none of the existing tests exercise
/// the keyed-services DI path — every consumer is either NSubstituted or directly
/// constructed in tests. A typo in a <c>[FromKeyedServices(...)]</c> attribute, or a host
/// that removes a keyed registration but leaves consumers behind, would silently break
/// at runtime with no test signal. This test plugs that gap with a reflection-based audit:
/// </para>
///
/// <list type="number">
///   <item>For every public/internal type in the Abstractions, Application, and Contracts.Domain
///         assemblies, find every constructor parameter annotated with
///         <see cref="FromKeyedServicesAttribute"/>.</item>
///   <item>Assert each consumed key is present in the host's expected registered-keys snapshot.</item>
///   <item>Assert each registered key has at least one consumer (catches dead registrations
///         that someone forgot to delete after removing the last consumer).</item>
/// </list>
///
/// <para>
/// The <see cref="HostRegisteredKeys"/> set below is a deliberate snapshot —
/// when <c>PaperbaseHostModule.ConfigureAI</c> adds or removes a
/// <c>AddKeyedChatClient(...)</c> call, this constant must be updated in the same change.
/// That coupling is the point: the test forces dev awareness when the host's keyed
/// registrations change.
/// </para>
/// </summary>
public class KeyedChatClientRegistration_Tests
{
    /// <summary>
    /// Snapshot of <c>PaperbaseHostModule.ConfigureAI</c>'s <c>AddKeyedChatClient(...)</c>
    /// calls. KEEP IN SYNC with that method.
    /// </summary>
    private static readonly HashSet<string> HostRegisteredKeys = new()
    {
        PaperbaseAIConsts.SummarizerChatClientKey,
        PaperbaseAIConsts.TitleGeneratorChatClientKey,
        PaperbaseAIConsts.StructuredChatClientKey,
    };

    /// <summary>
    /// Core production assemblies in the test's scope. Business-module assemblies
    /// (e.g. Contracts.Domain) are audited in their own test project — see
    /// <c>modules/contracts/test/Dignite.Paperbase.Contracts.Domain.Tests/Ai/</c> for the
    /// parallel audit. Splitting per-module avoids coupling Application.Tests to
    /// every module assembly via ProjectReference.
    /// </summary>
    private static readonly Assembly[] ProductionAssemblies =
    {
        typeof(PaperbaseAIConsts).Assembly,         // Dignite.Paperbase.Abstractions
        typeof(ChatAppService).Assembly,            // Dignite.Paperbase.Application
    };

    [Fact]
    public void Every_Consumed_Key_Is_Registered_By_The_Host()
    {
        var consumers = FindKeyedConsumers().ToList();
        consumers.ShouldNotBeEmpty(
            "The audit must find at least one [FromKeyedServices] consumer — otherwise the " +
            "scan is broken (wrong assemblies, attribute changed, etc.). Currently we expect " +
            "ChatAppService, ChatCompactionStrategyFactory, DocumentRerankWorkflow, " +
            "DocumentClassificationWorkflow, DocumentTextExtractionBackgroundJob, " +
            "ContractDocumentHandler — at minimum.");

        var orphans = consumers
            .Where(c => !HostRegisteredKeys.Contains(c.Key))
            .ToList();

        orphans.ShouldBeEmpty(
            "These [FromKeyedServices(...)] consumers reference keys NOT registered by " +
            "PaperbaseHostModule.ConfigureAI. Either add the AddKeyedChatClient call there, " +
            "or remove the [FromKeyedServices] consumer / fix the typo: " +
            string.Join("; ", orphans.Select(o => $"{o.TypeName}.{o.ParamName} = \"{o.Key}\"")));
    }

    [Fact]
    public void Every_Host_Registered_Key_Has_At_Least_One_Consumer()
    {
        var consumedKeys = FindKeyedConsumers().Select(c => c.Key).ToHashSet();

        var unused = HostRegisteredKeys
            .Where(key => !consumedKeys.Contains(key))
            .ToList();

        unused.ShouldBeEmpty(
            "These host-registered keyed IChatClient registrations have NO consumer anywhere " +
            "in production code. Either delete the AddKeyedChatClient call (dead registration), " +
            "or restore the missing consumer that was accidentally deleted: " +
            string.Join(", ", unused));
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
