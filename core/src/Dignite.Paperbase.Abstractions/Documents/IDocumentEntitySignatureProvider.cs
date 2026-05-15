using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 硬伤二 (L2 Phase 3): business module → L2 contract for multi-field "entity signature"
/// matching, complementing the single-field <see cref="IDocumentIdentifierProvider"/>.
///
/// <para>
/// <strong>When to implement a signature provider vs an identifier provider</strong>:
/// <list type="bullet">
/// <item>If your module has a single field whose value is unique to a business entity
/// (contract number, invoice number) → use <see cref="IDocumentIdentifierProvider"/>.</item>
/// <item>If your module's "same business entity" judgment requires AND-ing multiple fields
/// (parties + year, vendor + PO + date, project + version) → implement this contract instead.
/// Emit only when ALL fields are populated; empty fields turn the signature into noise.</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>DI</strong>: implement <see cref="Volo.Abp.DependencyInjection.ITransientDependency"/>;
/// L2 resolves <c>IEnumerable&lt;IDocumentEntitySignatureProvider&gt;</c> for cross-module fan-out.
/// </para>
///
/// <para>
/// <strong>Tenant isolation</strong>: implementations rely on ABP <c>IMultiTenant</c> ambient
/// filter (queries through the module's repository) — matches <see cref="IDocumentIdentifierProvider"/>'s
/// contract. <see cref="Documents.Pipelines.RelationDiscovery"/> background job sets
/// <c>CurrentTenant.Change(args.TenantId)</c> before invoking providers.
/// </para>
/// </summary>
public interface IDocumentEntitySignatureProvider
{
    /// <summary>
    /// Module-namespaced signature kinds this provider handles. L2 fan-out only routes a
    /// signature with <c>Kind = K</c> to providers that include K in this collection. Cross-module
    /// matching is impossible by design — a contract's <c>"Contracts.PartiesAndYear"</c> signature
    /// is never compared against an invoice's <c>"Invoices.VendorAndDate"</c> signature even if
    /// the field shapes happen to coincide.
    /// </summary>
    IReadOnlyCollection<string> SupportedSignatureKinds { get; }

    /// <summary>
    /// Returns the entity signatures the given document holds. Empty if the provider does not
    /// own the document, or if the document's typed record doesn't have all the required fields
    /// populated (incomplete signatures must NOT be emitted — see
    /// <see cref="DocumentEntitySignature"/> contract).
    /// </summary>
    Task<IReadOnlyList<DocumentEntitySignature>> GetSignaturesAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse lookup: documents in the current tenant whose own signature matches the
    /// supplied one. All fields must match (after the provider's own normalization);
    /// returns empty if any field is missing in the candidate.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindDocumentsBySignatureAsync(
        DocumentEntitySignature signature,
        CancellationToken cancellationToken = default);
}
