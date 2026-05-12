using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Chat;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Contracts.Contracts;
using Dignite.Paperbase.Contracts.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Linq;

namespace Dignite.Paperbase.Contracts.Chat;

/// <summary>
/// Contributes AI tools for document chat conversations scoped to
/// <see cref="ContractsDocumentTypes.General"/>:
/// <list type="bullet">
///   <item><c>search_contracts</c> — query the relational store by structured criteria</item>
///   <item><c>get_contract_detail</c> — fetch the full extracted field set for a single contract</item>
///   <item><c>get_contract_aggregate</c> — count + sum aggregate by party / date range / status, the
///         class of query that pure RAG cannot answer because cosine similarity over chunks
///         loses arithmetic semantics</item>
/// </list>
/// All tools fail closed: the caller must hold <see cref="ContractsPermissions.Contracts.Default"/>
/// and is restricted to the current tenant via an explicit <c>TenantId</c> predicate (no reliance on
/// the ambient ABP <c>DataFilter</c>).
/// </summary>
public class ContractChatToolContributor : IChatToolContributor, ITransientDependency
{
    private readonly IContractRepository _contractRepository;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IAuthorizationService _authorizationService;

    public ContractChatToolContributor(
        IContractRepository contractRepository,
        IAsyncQueryableExecuter asyncExecuter,
        IAuthorizationService authorizationService)
    {
        _contractRepository = contractRepository;
        _asyncExecuter = asyncExecuter;
        _authorizationService = authorizationService;
    }

    public virtual string DocumentTypeCode => ContractsDocumentTypes.General;

    public virtual IEnumerable<AIFunction> ContributeTools(
        ChatToolContext ctx,
        IChatToolFactory toolFactory)
    {
        var binding = new ContractToolBindings(
            _contractRepository,
            _asyncExecuter,
            ctx.TenantId,
            _authorizationService);

        yield return toolFactory.Create(
            ctx,
            binding.SearchAsync,
            name: "search_contracts",
            description:
                "Search contracts by structured criteria: contract number, party name, " +
                "date range, or amount range. " +
                "Returns matched document IDs and contract metadata summaries. " +
                "Use the returned document IDs to restrict further document content search to the relevant contracts.",
            // Issue #116: party / contract number are user-meaningful filters and OK to surface.
            // Dates / amount ranges are too noisy in a one-line label — caller can see them in
            // the audit log. The string is end-user facing, so keep it short and friendly.
            progressDescriber: DescribeSearch);

        yield return toolFactory.Create(
            ctx,
            binding.GetDetailAsync,
            name: "get_contract_detail",
            description:
                "Fetch the full extracted field set for a single contract by its document ID. " +
                "Use this after search_contracts narrows the candidate set, when the user asks " +
                "for fields not included in the search summary (governing law, auto-renewal, " +
                "termination notice days, effective date, etc.).",
            // documentId is opaque to users; generic label is more useful here than a UUID.
            progressDescriber: _ => "正在查询合同详情…");

        yield return toolFactory.Create(
            ctx,
            binding.GetAggregateAsync,
            name: "get_contract_aggregate",
            description:
                "Aggregate contract counts and total amounts grouped by currency, optionally " +
                "filtered by party name, signed-date range, or status. " +
                "Use this for arithmetic questions like \"how many active contracts with party X\" " +
                "or \"total signed amount in Q1\" — these cannot be answered by vector search " +
                "over document chunks.",
            progressDescriber: DescribeAggregate);
    }

    // Issue #130 (Codex review of #129): the previous implementations echoed raw
    // model-controlled `partyName` / `contractNumber` values into user-facing SSE
    // text. ToolCallStarted is emitted BEFORE the tool body's
    // ContractsPermissions.Contracts.Default check runs, so a user without that
    // permission could still observe contract-specific reflected text — and a
    // prompt-injected / hallucinated value would round-trip back to the UI.
    //
    // Until we add a separate post-authorization event type that's safe to
    // populate from real arguments, pre-execution descriptions stay generic and
    // never reflect anything from `arguments`. Filter shape (which field the
    // model chose) is also model-controlled but carries no value content.
    private static string DescribeSearch(IReadOnlyDictionary<string, object?> arguments)
    {
        var hasParty = !string.IsNullOrEmpty(TryGetString(arguments, "partyName"));
        var hasContractNumber = !string.IsNullOrEmpty(TryGetString(arguments, "contractNumber"));

        if (hasParty)
        {
            return "正在按甲方筛选合同…";
        }

        if (hasContractNumber)
        {
            return "正在按合同号查找合同…";
        }

        return "正在按条件筛选合同…";
    }

    private static string DescribeAggregate(IReadOnlyDictionary<string, object?> arguments)
        => "正在统计合同金额…";

    private static string? TryGetString(IReadOnlyDictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var value) ? value?.ToString() : null;

    // ── nested binding ───────────────────────────────────────────────────────

    /// <summary>
    /// Holds the bound context for the contributed AIFunctions.
    /// Factored into a class so parameter-level <see cref="DescriptionAttribute"/>s are
    /// accessible via reflection (lambda parameters cannot carry attributes in C#).
    /// </summary>
    private sealed class ContractToolBindings
    {
        private const int MaxResultRows = 20;

        private readonly IContractRepository _repo;
        private readonly IAsyncQueryableExecuter _executer;
        private readonly Guid? _tenantId;
        private readonly IAuthorizationService _authorizationService;

        public ContractToolBindings(
            IContractRepository repo,
            IAsyncQueryableExecuter executer,
            Guid? tenantId,
            IAuthorizationService authorizationService)
        {
            _repo = repo;
            _executer = executer;
            _tenantId = tenantId;
            _authorizationService = authorizationService;
        }

        public async Task<string> SearchAsync(
            [Description("Contract number or partial number to search for")]
            string? contractNumber = null,
            [Description("Party name — matches Party A, Party B, or counterparty (partial match)")]
            string? partyName = null,
            [Description("Earliest signed date in ISO 8601 format, e.g. 2024-01-01")]
            DateTime? signedDateFrom = null,
            [Description("Latest signed date in ISO 8601 format")]
            DateTime? signedDateTo = null,
            [Description("Earliest expiration date in ISO 8601 format")]
            DateTime? expirationDateFrom = null,
            [Description("Latest expiration date in ISO 8601 format")]
            DateTime? expirationDateTo = null,
            [Description("Minimum total contract amount")]
            decimal? amountMin = null,
            [Description("Maximum total contract amount")]
            decimal? amountMax = null,
            CancellationToken cancellationToken = default)
        {
            await _authorizationService.CheckAsync(ContractsPermissions.Contracts.Default);

            var queryable = await ScopedQueryableAsync();
            queryable = queryable.Where(c => !c.NeedsReview);

            if (!string.IsNullOrWhiteSpace(contractNumber))
                queryable = queryable.Where(c =>
                    c.ContractNumber != null && c.ContractNumber.Contains(contractNumber));

            if (!string.IsNullOrWhiteSpace(partyName))
                queryable = queryable.Where(c =>
                    (c.PartyAName != null && c.PartyAName.Contains(partyName)) ||
                    (c.PartyBName != null && c.PartyBName.Contains(partyName)) ||
                    (c.CounterpartyName != null && c.CounterpartyName.Contains(partyName)));

            if (signedDateFrom.HasValue)
                queryable = queryable.Where(c => c.SignedDate >= signedDateFrom);
            if (signedDateTo.HasValue)
                queryable = queryable.Where(c => c.SignedDate <= signedDateTo);

            if (expirationDateFrom.HasValue)
                queryable = queryable.Where(c => c.ExpirationDate >= expirationDateFrom);
            if (expirationDateTo.HasValue)
                queryable = queryable.Where(c => c.ExpirationDate <= expirationDateTo);

            if (amountMin.HasValue)
                queryable = queryable.Where(c => c.TotalAmount >= amountMin);
            if (amountMax.HasValue)
                queryable = queryable.Where(c => c.TotalAmount <= amountMax);

            var contracts = await _executer.ToListAsync(
                queryable.OrderByDescending(c => c.CreationTime).Take(MaxResultRows),
                cancellationToken);

            // Empty-result hint: when the structured filter matches nothing, return a
            // payload that explicitly directs the next step. Without this, models reading
            // bare `{documentIds:[], contracts:[]}` reliably conclude "answer is: nothing
            // exists" and skip vector retrieval — even when the system prompt tells them
            // to fall back. Tool-result instructions live IN the model's context and beat
            // system-prompt advisories when the two conflict. See trace
            // 82e2a5efa5b120441f1ccd6e334c6ee3 for the failure mode this fixes.
            //
            // The hint covers three real causes of empty result here:
            //   (a) the document has never been classified as a contract (extraction not
            //       run, or classified as a different type) — Contract entity doesn't exist
            //   (b) the contract exists but NeedsReview=true (the !NeedsReview WHERE clause
            //       above hides pending extractions from list-style search)
            //   (c) the party name / contract number was extracted with slightly different
            //       spelling than the filter string
            if (contracts.Count == 0)
            {
                var emptyHint = new
                {
                    documentIds = System.Array.Empty<System.Guid>(),
                    contracts = System.Array.Empty<object>(),
                    note = "No contracts matched the structured filters. This does NOT mean " +
                           "the document is absent. Before answering 'not found', call " +
                           "search_paperbase_documents with the same query as a semantic " +
                           "search — the document may exist in the vector store but: " +
                           "(1) it hasn't been classified as a contract yet, " +
                           "(2) its extraction is pending review and is excluded from this " +
                           "structured search, or " +
                           "(3) the party name / contract number was extracted with " +
                           "different spelling than your filter."
                };
                return JsonSerializer.Serialize(emptyHint);
            }

            // Issue #148: LLM-extracted free-text fields (title / party names / summary /
            // contractNumber) are user-controlled at the source (uploaded document text) and
            // can carry indirect prompt injection payloads. Wrap them in <field>…</field>
            // before they enter the model context. Structural fields (IDs, dates, amounts,
            // enums, currency code) stay bare.
            var result = new
            {
                documentIds = contracts.Select(c => c.DocumentId).ToList(),
                contracts = contracts.Select(c => new
                {
                    documentId = c.DocumentId,
                    contractNumber = PromptBoundary.WrapField(c.ContractNumber),
                    title = PromptBoundary.WrapField(c.Title),
                    partyAName = PromptBoundary.WrapField(c.PartyAName),
                    partyBName = PromptBoundary.WrapField(c.PartyBName),
                    counterpartyName = PromptBoundary.WrapField(c.CounterpartyName),
                    totalAmount = c.TotalAmount,
                    currency = c.Currency,
                    signedDate = c.SignedDate?.ToString("yyyy-MM-dd"),
                    expirationDate = c.ExpirationDate?.ToString("yyyy-MM-dd"),
                    summary = PromptBoundary.WrapField(c.Summary)
                }).ToList()
            };

            return JsonSerializer.Serialize(result);
        }

        public async Task<string> GetDetailAsync(
            [Description("Document ID returned by search_contracts")]
            Guid documentId,
            CancellationToken cancellationToken = default)
        {
            await _authorizationService.CheckAsync(ContractsPermissions.Contracts.Default);

            var queryable = await ScopedQueryableAsync();
            var contract = await _executer.FirstOrDefaultAsync(
                queryable.Where(c => c.DocumentId == documentId),
                cancellationToken);

            if (contract == null)
            {
                // Same hint-in-tool-result pattern as the empty branch in SearchAsync:
                // tell the model explicitly what 'not found' here means and what to try next.
                // Note get_contract_detail does NOT have the !NeedsReview filter that
                // search_contracts has, so a null here means the documentId really isn't
                // in the Contract table at all (vs filtered out for pending review).
                return JsonSerializer.Serialize(new
                {
                    found = false,
                    documentId,
                    note = "No Contract record exists for this documentId. The document may " +
                           "still be in the vector store — try search_paperbase_documents " +
                           "with the user's query terms to read its content directly. Common " +
                           "causes: the document was not classified as a contract type, or " +
                           "classification ran but contract field extraction failed."
                });
            }

            // Issue #148: same field-level injection defense as SearchAsync.
            var detail = new
            {
                found = true,
                documentId = contract.DocumentId,
                title = PromptBoundary.WrapField(contract.Title),
                contractNumber = PromptBoundary.WrapField(contract.ContractNumber),
                partyAName = PromptBoundary.WrapField(contract.PartyAName),
                partyBName = PromptBoundary.WrapField(contract.PartyBName),
                counterpartyName = PromptBoundary.WrapField(contract.CounterpartyName),
                totalAmount = contract.TotalAmount,
                currency = contract.Currency,
                signedDate = contract.SignedDate?.ToString("yyyy-MM-dd"),
                effectiveDate = contract.EffectiveDate?.ToString("yyyy-MM-dd"),
                expirationDate = contract.ExpirationDate?.ToString("yyyy-MM-dd"),
                autoRenewal = contract.AutoRenewal,
                terminationNoticeDays = contract.TerminationNoticeDays,
                governingLaw = PromptBoundary.WrapField(contract.GoverningLaw),
                status = contract.Status.ToString(),
                summary = PromptBoundary.WrapField(contract.Summary),
                needsReview = contract.NeedsReview,
                reviewStatus = contract.ReviewStatus.ToString(),
                extractionConfidence = contract.ExtractionConfidence
            };

            return JsonSerializer.Serialize(detail);
        }

        public async Task<string> GetAggregateAsync(
            [Description("Optional party name filter — matches Party A, Party B, or counterparty (partial match)")]
            string? partyName = null,
            [Description("Earliest signed date in ISO 8601 format, e.g. 2024-01-01")]
            DateTime? signedDateFrom = null,
            [Description("Latest signed date in ISO 8601 format")]
            DateTime? signedDateTo = null,
            [Description("Optional contract status filter: Draft, Active, Expired, Terminated, or Archived")]
            string? status = null,
            CancellationToken cancellationToken = default)
        {
            await _authorizationService.CheckAsync(ContractsPermissions.Contracts.Default);

            var queryable = await ScopedQueryableAsync();
            queryable = queryable.Where(c => !c.NeedsReview);

            if (!string.IsNullOrWhiteSpace(partyName))
                queryable = queryable.Where(c =>
                    (c.PartyAName != null && c.PartyAName.Contains(partyName)) ||
                    (c.PartyBName != null && c.PartyBName.Contains(partyName)) ||
                    (c.CounterpartyName != null && c.CounterpartyName.Contains(partyName)));

            if (signedDateFrom.HasValue)
                queryable = queryable.Where(c => c.SignedDate >= signedDateFrom);
            if (signedDateTo.HasValue)
                queryable = queryable.Where(c => c.SignedDate <= signedDateTo);

            if (!string.IsNullOrWhiteSpace(status)
                && Enum.TryParse<ContractStatus>(status, ignoreCase: true, out var parsedStatus))
            {
                queryable = queryable.Where(c => c.Status == parsedStatus);
            }

            var grouped = queryable
                .GroupBy(c => c.Currency)
                .Select(g => new
                {
                    currency = g.Key,
                    count = g.Count(),
                    totalAmount = g.Sum(c => c.TotalAmount ?? 0m)
                });

            var buckets = await _executer.ToListAsync(grouped, cancellationToken);

            var result = new
            {
                groupBy = "currency",
                buckets,
                grandCount = buckets.Sum(b => b.count)
            };

            return JsonSerializer.Serialize(result);
        }

        private async Task<IQueryable<Contract>> ScopedQueryableAsync()
        {
            var queryable = await _repo.GetQueryableAsync();

            // Explicit tenant predicate — never rely solely on ABP's ambient DataFilter,
            // which can be disabled on background threads or non-HTTP code paths.
            return _tenantId.HasValue
                ? queryable.Where(c => c.TenantId == _tenantId)
                : queryable.Where(c => c.TenantId == null);
        }
    }
}
