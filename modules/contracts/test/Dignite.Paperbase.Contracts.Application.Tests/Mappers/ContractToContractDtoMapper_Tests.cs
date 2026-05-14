using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Contracts;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Contracts;

public class ContractToContractDtoMapper_Tests : PaperbaseContractsApplicationTestBase<PaperbaseContractsApplicationTestModule>
{
    private readonly ContractManager _contractManager;
    private readonly ContractToContractDtoMapper _mapper;

    public ContractToContractDtoMapper_Tests()
    {
        _contractManager = GetRequiredService<ContractManager>();
        _mapper = GetRequiredService<ContractToContractDtoMapper>();
    }

    [Fact]
    public async Task Should_Map_Contract_To_Dto()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var contract = await _contractManager.CreateAsync(
            documentId,
            PaperbaseContractsDocumentTypes.General,
            CreateFields());

        // Act
        var dto = _mapper.Map(contract);

        // Assert
        dto.Id.ShouldBe(contract.Id);
        dto.DocumentId.ShouldBe(documentId);
        dto.DocumentTypeCode.ShouldBe(PaperbaseContractsDocumentTypes.General);
        dto.Title.ShouldBe("業務委託契約書");
        dto.PartyBName.ShouldBe("株式会社サンプル");
        dto.TotalAmount.ShouldBe(1200000m);
        dto.Currency.ShouldBe("JPY");
        dto.Status.ShouldBe(ContractStatus.Draft);
        // Fresh AI extraction always lands as pending review.
        dto.NeedsReview.ShouldBeTrue();
        dto.ReviewStatus.ShouldBe(ContractReviewStatus.Pending);
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
