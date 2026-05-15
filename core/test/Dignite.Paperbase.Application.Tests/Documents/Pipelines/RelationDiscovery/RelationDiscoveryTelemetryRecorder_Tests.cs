using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Direct tests against <see cref="RelationDiscoveryTelemetryRecorder"/> using a
/// <see cref="MeterListener"/> to capture emitted measurements. Meter is process-wide
/// (singleton), so listener filters by instrument name to avoid noise from other tests.
/// </summary>
public class RelationDiscoveryTelemetryRecorder_Tests
{
    private readonly RelationDiscoveryTelemetryRecorder _recorder = new(NullLogger<RelationDiscoveryTelemetryRecorder>.Instance);

    [Fact]
    public void RecordRun_Should_Emit_Counter_With_Result_Tag()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.runs.total");

        _recorder.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = Guid.NewGuid(),
            Result = RelationDiscoveryRunResult.Succeeded,
            CreatedCount = 0,
            TotalDurationMs = 12.5
        });

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Value.ShouldBe(1L);
        capture.Measurements[0].Tags["result"].ShouldBe("Succeeded");
    }

    [Fact]
    public void RecordRun_Should_Record_Created_Histogram_When_Set()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.created");

        _recorder.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = Guid.NewGuid(),
            Result = RelationDiscoveryRunResult.Succeeded,
            CreatedCount = 3
        });

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Value.ShouldBe(3L);
    }

    [Fact]
    public void RecordRun_Should_Emit_Duration_Histogram_With_Phase_Tags()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.duration");

        _recorder.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = Guid.NewGuid(),
            Result = RelationDiscoveryRunResult.Succeeded,
            DiscoveryDurationMs = 5.0,
            TotalDurationMs = 1505.0,
        });

        capture.Measurements.Count.ShouldBe(2);
        capture.Measurements.ShouldContain(m => (string)m.Tags["phase"] == "discovery" && Math.Abs(m.ValueAsDouble - 5.0) < 0.01);
        capture.Measurements.ShouldContain(m => (string)m.Tags["phase"] == "total" && Math.Abs(m.ValueAsDouble - 1505.0) < 0.01);
    }

    [Fact]
    public void RecordSuggestionConfirmed_Should_Tag_Source()
    {
        using var capture = new MeterCapture("paperbase.relation.suggestion.confirmed");

        _recorder.RecordSuggestionConfirmed(RelationSource.AiSuggested);

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Tags["source"].ShouldBe("AiSuggested");
    }

    [Fact]
    public void RecordSuggestionRejected_Should_Tag_Source()
    {
        using var capture = new MeterCapture("paperbase.relation.suggestion.rejected");

        _recorder.RecordSuggestionRejected(RelationSource.AiSuggested);

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Tags["source"].ShouldBe("AiSuggested");
    }

    [Fact]
    public void RecordIdentifiersByProvider_Should_Tag_Provider()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.identifiers_by_provider");

        _recorder.RecordIdentifiersByProvider("ContractIdentifierProvider", 3);

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Value.ShouldBe(3L);
        capture.Measurements[0].Tags["provider"].ShouldBe("ContractIdentifierProvider");
    }

    [Fact]
    public void RecordOrphanDocument_Should_Increment_Counter()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.orphan_documents");

        _recorder.RecordOrphanDocument();

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Value.ShouldBe(1L);
    }

    [Fact]
    public void RecordHighAmbiguityIdentifier_Should_Tag_Type()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.high_ambiguity_identifiers");

        _recorder.RecordHighAmbiguityIdentifier("ContractNumber", "HT2024001", peerCount: 42);

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Value.ShouldBe(1L);
        capture.Measurements[0].Tags["type"].ShouldBe("ContractNumber");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposable scope that captures all measurements emitted to a single instrument
    /// on the RelationDiscovery meter. Filters by instrument name so other tests'
    /// emissions on the same meter don't pollute results.
    /// </summary>
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener;
        public List<CapturedMeasurement> Measurements { get; } = new();

        public MeterCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == RelationDiscoveryTelemetryRecorder.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
                Measurements.Add(new CapturedMeasurement(value, value, ToDictionary(tags))));
            _listener.SetMeasurementEventCallback<double>((inst, value, tags, state) =>
                Measurements.Add(new CapturedMeasurement((long)value, value, ToDictionary(tags))));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>(tags.Length);
            foreach (var kv in tags)
            {
                dict[kv.Key] = kv.Value;
            }
            return dict;
        }
    }

    private sealed record CapturedMeasurement(long Value, double ValueAsDouble, IReadOnlyDictionary<string, object?> Tags);
}
