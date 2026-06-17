using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.DocumentAI.Documents.Pipelines.Segmentation;

/// <summary>
/// Born-digital container segmentation (#346): one LLM pass over a container's Markdown that proposes where each
/// constituent document begins. Per the locked design (decision in the #346 Decision Log) the LLM returns only
/// <b>verbatim start markers</b> + an is-document flag; <see cref="MarkdownSlicer"/> does the deterministic
/// cutting, so the LLM never regenerates content and the split is verifiable.
/// <para>
/// Mirrors <see cref="Classification.DocumentClassificationWorkflow"/>: tool-free, structured-output, routed
/// through the dedicated keyed <see cref="DocumentAIConsts.StructuredChatClientKey"/> client; no
/// <c>AIContextProviders</c> (channel layer, not RAG); the container Markdown is wrapped with
/// <see cref="PromptBoundary.WrapDocument"/> and the boundary rule is appended to the instructions.
/// </para>
/// </summary>
public class DocumentSegmentationWorkflow : ITransientDependency
{
    private readonly IChatClient _chatClient;
    private readonly IPromptProvider _promptProvider;
    private readonly DocumentAIBehaviorOptions _options;

    public ILogger<DocumentSegmentationWorkflow> Logger { get; set; }
        = NullLogger<DocumentSegmentationWorkflow>.Instance;

    public DocumentSegmentationWorkflow(
        [FromKeyedServices(DocumentAIConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<DocumentAIBehaviorOptions> options,
        IPromptProvider promptProvider)
    {
        _chatClient = chatClient;
        _promptProvider = promptProvider;
        _options = options.Value;
    }

    public virtual async Task<DocumentSegmentationOutcome> RunAsync(
        string markdown,
        CancellationToken cancellationToken = default)
    {
        var outcome = new DocumentSegmentationOutcome();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return outcome;
        }

        // Unlike classification (which truncates to the leading prefix), segmentation feeds the WHOLE Markdown:
        // constituent boundaries can be anywhere, and a truncated tail would silently lose the last documents.
        // Output stays small regardless of input size because only short verbatim markers are returned.
        var userMessage = $$"""
                ## Document Markdown
                {{PromptBoundary.WrapDocument(markdown)}}
                """;

        var template = _promptProvider.GetSegmentationPrompt(_options.DefaultLanguage);
        AIAgent agent = new ChatClientAgent(
            _chatClient,
            new ChatClientAgentOptions
            {
                Name = "DocumentAIDocumentSegmenter",
                ChatOptions = new ChatOptions
                {
                    Instructions = template.SystemInstructions + " " + PromptBoundary.BoundaryRule
                },
                UseProvidedChatClientAsIs = true
            })
            .AsBuilder()
            .UseOpenTelemetry()
            .Build();

        var response = await agent.RunAsync<SegmentationResponse>(
            userMessage,
            cancellationToken: cancellationToken);

        var parsed = response.Result;
        var droppedBlankMarkers = 0;
        if (parsed?.Segments != null)
        {
            foreach (var segment in parsed.Segments)
            {
                // A blank marker cannot be located in the Markdown, so it would only poison the slice; drop it
                // here and let MarkdownSlicer's verification decide whether the remaining set is trustworthy.
                if (!string.IsNullOrWhiteSpace(segment.StartMarker))
                {
                    outcome.Boundaries.Add(new SegmentBoundary(segment.StartMarker, segment.IsDocument));
                }
                else
                {
                    droppedBlankMarkers++;
                }
            }
        }

        // Drift visibility (same discipline as the other structured paths): surface what the LLM returned so a
        // systematic regression — empty output, or all-blank / unmatchable markers — is diagnosable at the call
        // site, not only as a downstream SegmentationIncomplete review flag. MarkdownSlicer then verifies each
        // marker verbatim against the Markdown.
        Logger.LogInformation(
            "Segmentation proposed {BoundaryCount} boundaries ({DroppedBlankMarkers} blank markers dropped) for a {Length}-character container.",
            outcome.Boundaries.Count, droppedBlankMarkers, markdown.Length);

        return outcome;
    }

    private sealed class SegmentationResponse
    {
        public List<SegmentItem> Segments { get; set; } = new();

        public sealed class SegmentItem
        {
            /// <summary>The verbatim first line / opening snippet of the constituent, copied exactly from the Markdown.</summary>
            public string StartMarker { get; set; } = default!;

            /// <summary><c>true</c> if the slice is itself a document; <c>false</c> for a cover / index / transmittal page.</summary>
            public bool IsDocument { get; set; }
        }
    }
}

public class DocumentSegmentationOutcome
{
    /// <summary>Ordered constituent boundaries proposed by the LLM; fed to <see cref="MarkdownSlicer.TrySlice"/>.</summary>
    public List<SegmentBoundary> Boundaries { get; } = new();
}
