using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Contracts;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Contracts;

public class ContractManager_Tests : PaperbaseContractsDomainTestBase<PaperbaseContractsDomainTestModule>
{
    private readonly ContractManager _contractManager;

    public ContractManager_Tests()
    {
        _contractManager = GetRequiredService<ContractManager>();
    }

    [Fact]
    public async Task Should_Create_Contract_From_Extracted_Fields()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var fields = CreateFields();

        // Act
        var contract = await _contractManager.CreateAsync(
            documentId,
            PaperbaseContractsDocumentTypes.General,
            fields);

        // Assert
        contract.Id.ShouldNotBe(Guid.Empty);
        contract.TenantId.ShouldBeNull();
        contract.DocumentId.ShouldBe(documentId);
        contract.DocumentTypeCode.ShouldBe(PaperbaseContractsDocumentTypes.General);
        contract.Status.ShouldBe(ContractStatus.Draft);
        contract.Title.ShouldBe(fields.Title);
        contract.PartyBName.ShouldBe(fields.PartyBName);
        contract.TotalAmount.ShouldBe(fields.TotalAmount);
        // Fresh AI extraction always lands as pending review.
        contract.NeedsReview.ShouldBeTrue();
        contract.ReviewStatus.ShouldBe(ContractReviewStatus.Pending);
    }

    [Fact]
    public async Task Should_Update_Extracted_Fields_And_Confirm()
    {
        // Arrange
        var contract = await _contractManager.CreateAsync(
            Guid.NewGuid(),
            PaperbaseContractsDocumentTypes.General,
            CreateFields());

        var updatedFields = CreateFields();
        updatedFields.Title = "秘密保持契約書";
        updatedFields.PartyBName = "株式会社アップデート";
        updatedFields.TotalAmount = 250000m;

        // Act — re-extraction always flips to Pending regardless of prior review state.
        contract.UpdateExtractedFields(updatedFields);

        // Assert
        contract.Title.ShouldBe("秘密保持契約書");
        contract.PartyBName.ShouldBe("株式会社アップデート");
        contract.TotalAmount.ShouldBe(250000m);
        contract.NeedsReview.ShouldBeTrue();
        contract.ReviewStatus.ShouldBe(ContractReviewStatus.Pending);

        // Act
        contract.Confirm();

        // Assert
        contract.NeedsReview.ShouldBeFalse();
        contract.ReviewStatus.ShouldBe(ContractReviewStatus.Confirmed);
        contract.Status.ShouldBe(ContractStatus.Active);
    }

    [Fact]
    public async Task Should_Archive_And_Restore_As_Draft_For_Document_Recycle_Bin()
    {
        var contract = await _contractManager.CreateAsync(
            Guid.NewGuid(),
            PaperbaseContractsDocumentTypes.General,
            CreateFields());
        contract.Confirm();

        contract.ArchiveBecauseDocumentDeleted();
        contract.Status.ShouldBe(ContractStatus.Archived);

        contract.RestoreBecauseDocumentRestored();
        contract.Status.ShouldBe(ContractStatus.Draft);
        contract.NeedsReview.ShouldBeTrue();
        contract.ReviewStatus.ShouldBe(ContractReviewStatus.Pending);
    }

    private static ContractFields CreateFields()
    {
        return new ContractFields
        {
            Title = "業務委託契約書",
            ContractNumber = "CNT-2026-001",
            PartyAName = "株式会社ディグナイト",
            PartyBName = "株式会社サンプル",
            SignedDate = new DateTime(2026, 4, 1),
            EffectiveDate = new DateTime(2026, 4, 1),
            ExpirationDate = new DateTime(2027, 3, 31),
            TotalAmount = 1200000m,
            Currency = "JPY",
            ExtractionConfidence = 0.9
        };
    }
}
