using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Structured telemetry for RelationDiscovery — counters / histograms exported via
/// <see cref="System.Diagnostics.Metrics"/> and a paired structured log line per metric event.
///
/// <para>
/// <strong>Why a project-specific recorder vs ABP audit log</strong>: BackgroundJob runs are not in
/// HTTP context and have no audit scope by default; we need metrics, not audit rows. The
/// log lines are kept (not replaced) to preserve developer-grade observability.
/// </para>
///
/// <para>
/// <strong>Tag policy</strong>: tags are low-cardinality enums or buckets. <c>tenant_id</c> is
/// intentionally NOT a tag (would cause cardinality explosion in multi-tenant deployments
/// and isn't needed for ops dashboards — per-tenant drill-down via traces / logs instead).
/// </para>
/// </summary>
public class RelationDiscoveryTelemetryRecorder : ISingletonDependency
{
    public const string MeterName = "Dignite.Paperbase.Documents.RelationDiscovery";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> RunsTotal = Meter.CreateCounter<long>(
        "paperbase.relation_discovery.runs.total",
        description: "RelationDiscovery background job executions, by result (succeeded / failed / document_missing).");

    private static readonly Histogram<long> RelationsCreated = Meter.CreateHistogram<long>(
        "paperbase.relation_discovery.created",
        description: "AiSuggested DocumentRelations created per run.");

    /// <summary>
    /// Visibility: how many identifiers each provider contributed for a given source
    /// document. Tag <c>provider</c> is the provider type name. Use this to spot a provider
    /// that suddenly stops producing identifiers (LLM extraction regressed, schema migration
    /// dropped fields, etc.). Recorded once per (run, provider) pair regardless of count;
    /// the histogram value is the count.
    /// </summary>
    private static readonly Histogram<long> IdentifiersByProvider = Meter.CreateHistogram<long>(
        "paperbase.relation_discovery.identifiers_by_provider",
        description: "Number of identifiers emitted per provider per source document. Tags: provider.");

    /// <summary>
    /// Documents that produced zero identifiers across all providers. A spike means either
    /// no business module owns these documents OR business-module extraction is failing
    /// silently (LLM down, wrong fields, etc.). Either way it's the "RelationDiscovery
    /// looks dead" signal operators need.
    /// </summary>
    private static readonly Counter<long> OrphanDocuments = Meter.CreateCounter<long>(
        "paperbase.relation_discovery.orphan_documents",
        description: "Documents that produced 0 identifiers (no module owns them, or extraction failed).");

    /// <summary>
    /// High-ambiguity identifiers — an identifier value that matched more than
    /// <see cref="HighAmbiguityPeerThreshold"/> peer documents in a single run. Tag
    /// <c>type</c> identifies which type is ambiguous; repeated hits over time identify
    /// identifier categories that should be excluded (the way <c>ContractIdentifierProvider</c>
    /// already excludes PartyName).
    /// </summary>
    private static readonly Counter<long> HighAmbiguityIdentifiers = Meter.CreateCounter<long>(
        "paperbase.relation_discovery.high_ambiguity_identifiers",
        description: "Identifier values that matched too many peers (>= threshold). Tags: type.");

    /// <summary>Threshold for an identifier to be flagged as high-ambiguity. Aggressive on
    /// purpose — once an identifier matches this many distinct peers in one run it's almost
    /// certainly noise (e.g. LLM hallucinated a common word as a contract number).</summary>
    public const int HighAmbiguityPeerThreshold = 10;

    private static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>(
        "paperbase.relation_discovery.duration",
        unit: "ms",
        description: "Wall-clock duration per RelationDiscovery run.");

    /// <summary>
    /// AiSuggested → Manual conversions (user clicked Confirm). The ground-truth quality
    /// signal: paired with <see cref="SuggestionRejected"/>, <c>accept_rate = confirmed /
    /// (confirmed + rejected)</c> tells you whether the AI suggestions are useful.
    /// </summary>
    private static readonly Counter<long> SuggestionConfirmed = Meter.CreateCounter<long>(
        "paperbase.relation.suggestion.confirmed",
        description: "User accepted an AiSuggested DocumentRelation (Confirm). Tag: source.");

    /// <summary>
    /// AiSuggested deletions (user clicked Delete on an AI-suggested relation).
    /// </summary>
    private static readonly Counter<long> SuggestionRejected = Meter.CreateCounter<long>(
        "paperbase.relation.suggestion.rejected",
        description: "User deleted an AiSuggested DocumentRelation (Delete). Tag: source.");

    private readonly ILogger<RelationDiscoveryTelemetryRecorder> _logger;

    public RelationDiscoveryTelemetryRecorder(ILogger<RelationDiscoveryTelemetryRecorder> logger)
    {
        _logger = logger;
    }

    public virtual void RecordRun(RelationDiscoveryRunMetrics metrics)
    {
        RunsTotal.Add(1, new KeyValuePair<string, object?>("result", metrics.Result.ToString()));

        if (metrics.CreatedCount.HasValue)
        {
            RelationsCreated.Record(metrics.CreatedCount.Value);
        }

        if (metrics.DiscoveryDurationMs.HasValue)
        {
            RunDuration.Record(metrics.DiscoveryDurationMs.Value, new KeyValuePair<string, object?>("phase", "discovery"));
        }
        if (metrics.TotalDurationMs.HasValue)
        {
            RunDuration.Record(metrics.TotalDurationMs.Value, new KeyValuePair<string, object?>("phase", "total"));
        }

        if (metrics.Result == RelationDiscoveryRunResult.Succeeded)
        {
            _logger.LogInformation(
                "RelationDiscovery run succeeded. DocumentId={DocumentId} Created={Created} DiscoveryMs={DiscoveryMs} TotalMs={TotalMs}",
                metrics.DocumentId,
                metrics.CreatedCount,
                metrics.DiscoveryDurationMs,
                metrics.TotalDurationMs);
        }
        else
        {
            _logger.LogWarning(
                "RelationDiscovery run did not complete normally. DocumentId={DocumentId} Result={Result} FailureReason={FailureReason} TotalMs={TotalMs}",
                metrics.DocumentId,
                metrics.Result,
                metrics.FailureReason,
                metrics.TotalDurationMs);
        }
    }

    public virtual void RecordSuggestionConfirmed(RelationSource originalSource)
    {
        SuggestionConfirmed.Add(1,
            new KeyValuePair<string, object?>("source", originalSource.ToString()));
    }

    public virtual void RecordSuggestionRejected(RelationSource originalSource)
    {
        SuggestionRejected.Add(1,
            new KeyValuePair<string, object?>("source", originalSource.ToString()));
    }

    /// <summary>
    /// Per-provider identifier contribution. Called once per (run, provider) pair.
    /// <paramref name="count"/> may be 0 — recording 0s lets dashboards see "this provider
    /// exists but didn't fire for this document" vs "this provider isn't installed."
    /// </summary>
    public virtual void RecordIdentifiersByProvider(string providerName, int count)
    {
        IdentifiersByProvider.Record(count,
            new KeyValuePair<string, object?>("provider", providerName));
    }

    /// <summary>
    /// A source document arrived with zero identifiers across all providers.
    /// </summary>
    public virtual void RecordOrphanDocument()
    {
        OrphanDocuments.Add(1);
    }

    /// <summary>
    /// A single (type, value) identifier matched too many peer documents in one run —
    /// almost certainly noise. <paramref name="peerCount"/> goes to the structured log
    /// so operators can see the egregious values.
    /// </summary>
    public virtual void RecordHighAmbiguityIdentifier(string identifierType, string normalizedValue, int peerCount)
    {
        HighAmbiguityIdentifiers.Add(1,
            new KeyValuePair<string, object?>("type", identifierType));
        _logger.LogWarning(
            "RelationDiscovery: high-ambiguity identifier matched {PeerCount} peers (threshold {Threshold}). " +
            "Type={IdentifierType} NormalizedValue={NormalizedValue}. Treat as noise candidate; " +
            "consider excluding this type from the provider's SupportedIdentifierTypes.",
            peerCount, HighAmbiguityPeerThreshold, identifierType, normalizedValue);
    }
}

/// <summary>Per-run metrics emitted by <see cref="RelationDiscoveryBackgroundJob"/> at completion.</summary>
public sealed record RelationDiscoveryRunMetrics
{
    public required Guid DocumentId { get; init; }
    public required RelationDiscoveryRunResult Result { get; init; }

    /// <summary>Number of AiSuggested relations created (null = discovery didn't run, e.g. document missing).</summary>
    public int? CreatedCount { get; init; }

    /// <summary>Wall-clock duration of the discovery phase (excluding pipeline-run bookkeeping).</summary>
    public double? DiscoveryDurationMs { get; init; }

    /// <summary>Total wall-clock duration including pipeline-run bookkeeping.</summary>
    public double? TotalDurationMs { get; init; }

    /// <summary>Set when <see cref="Result"/> is <see cref="RelationDiscoveryRunResult.Failed"/>.</summary>
    public string? FailureReason { get; init; }
}

public enum RelationDiscoveryRunResult
{
    Succeeded = 0,
    Failed = 1,
    DocumentMissing = 2
}
