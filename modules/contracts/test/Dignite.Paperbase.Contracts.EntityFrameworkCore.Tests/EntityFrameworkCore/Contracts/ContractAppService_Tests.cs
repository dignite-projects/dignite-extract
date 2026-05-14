using System;
using System.IO;
using System.Threading.Tasks;
using Dignite.Paperbase.Contracts;
using Dignite.Paperbase.Contracts.Dtos;
using Shouldly;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Paperbase.Contracts.EntityFrameworkCore.Contracts;

public class ContractAppService_Tests : PaperbaseContractsEntityFrameworkCoreTestBase
{
    private readonly IContractAppService _appService;
    private readonly ContractManager _contractManager;
    private readonly IContractRepository _contractRepository;

    public ContractAppService_Tests()
    {
        _appService = GetRequiredService<IContractAppService>();
        _contractManager = GetRequiredService<ContractManager>();
        _contractRepository = GetRequiredService<IContractRepository>();
    }

    // ────────────────────────────────────────────────────────────────────────────
    // GetAsync
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Should_Get_Contract_By_Id()
    {
        var contract = await CreateAndSaveAsync("甲公司", 1_000_000m, new DateTime(2027, 3, 31), needsReview: true);

        var dto = await _appService.GetAsync(contract.Id);

        dto.ShouldNotBeNull();
        dto.Id.ShouldBe(contract.Id);
        dto.PartyBName.ShouldBe("甲公司");
        dto.TotalAmount.ShouldBe(1_000_000m);
        dto.Status.ShouldBe(ContractStatus.Draft);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // GetListAsync — filters
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetList_Returns_All_When_No_Filter()
    {
        await CreateAndSaveAsync("甲公司", 1_000_000m, new DateTime(2027, 3, 31), false);
        await CreateAndSaveAsync("乙公司", 2_000_000m, new DateTime(2027, 6, 30), false);

        var result = await _appService.GetListAsync(new GetContractListInput { MaxResultCount = 100 });

        result.TotalCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetList_Filters_By_ExpirationDate_Range()
    {
        await CreateAndSaveAsync("早到期公司", 1_000_000m, new DateTime(2027, 1, 1), false);
        await CreateAndSaveAsync("中到期公司", 2_000_000m, new DateTime(2028, 1, 1), false);
        await CreateAndSaveAsync("晚到期公司", 3_000_000m, new DateTime(2029, 1, 1), false);

        var result = await _appService.GetListAsync(new GetContractListInput
        {
            ExpirationDateFrom = new DateTime(2027, 6, 1),
            ExpirationDateTo = new DateTime(2028, 6, 1),
            MaxResultCount = 100
        });

        result.Items.ShouldContain(c => c.PartyBName == "中到期公司");
        result.Items.ShouldNotContain(c => c.PartyBName == "早到期公司");
        result.Items.ShouldNotContain(c => c.PartyBName == "晚到期公司");
    }

    [Fact]
    public async Task GetList_Filters_By_NeedsReview_True()
    {
        await CreateAndSaveAsync("需审核公司", 1_000_000m, new DateTime(2027, 3, 31), needsReview: true);
        await CreateAndSaveAsync("普通公司", 2_000_000m, new DateTime(2027, 6, 30), needsReview: false);

        var result = await _appService.GetListAsync(new GetContractListInput
        {
            NeedsReview = true,
            MaxResultCount = 100
        });

        result.Items.ShouldAllBe(c => c.NeedsReview);
        result.Items.ShouldContain(c => c.PartyBName == "需审核公司");
        result.Items.ShouldNotContain(c => c.PartyBName == "普通公司");
    }

    [Fact]
    public async Task GetList_Filters_By_TotalAmount_Range()
    {
        await CreateAndSaveAsync("小额公司", 100_000m, new DateTime(2027, 3, 31), false);
        await CreateAndSaveAsync("中额公司", 500_000m, new DateTime(2027, 3, 31), false);
        await CreateAndSaveAsync("大额公司", 5_000_000m, new DateTime(2027, 3, 31), false);

        var result = await _appService.GetListAsync(new GetContractListInput
        {
            TotalAmountMin = 200_000m,
            TotalAmountMax = 1_000_000m,
            MaxResultCount = 100
        });

        result.Items.ShouldContain(c => c.PartyBName == "中额公司");
        result.Items.ShouldNotContain(c => c.PartyBName == "小额公司");
        result.Items.ShouldNotContain(c => c.PartyBName == "大额公司");
    }

    [Fact]
    public async Task GetList_Filters_By_DocumentId()
    {
        var targetDocId = Guid.NewGuid();
        await CreateAndSaveAsync("目标合同公司", 1_000_000m, new DateTime(2027, 3, 31), false, documentId: targetDocId);
        await CreateAndSaveAsync("其他合同公司", 2_000_000m, new DateTime(2027, 3, 31), false);

        var result = await _appService.GetListAsync(new GetContractListInput
        {
            DocumentId = targetDocId,
            MaxResultCount = 100
        });

        result.TotalCount.ShouldBe(1);
        result.Items[0].PartyBName.ShouldBe("目标合同公司");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // UpdateAsync
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Should_Update_Contract_Fields()
    {
        var contract = await CreateAndSaveAsync("旧公司", 1_000_000m, new DateTime(2027, 3, 31), true);

        // PUT semantics: full snapshot of all editable fields. Build by reading current state,
        // then changing only the fields the user is correcting — same pattern the detail form uses.
        var before = await _appService.GetAsync(contract.Id);
        var snapshot = ToFullSnapshot(before);
        snapshot.Title = "更新后标题";
        snapshot.PartyBName = "新公司";
        snapshot.TotalAmount = 2_000_000m;
        snapshot.ExpirationDate = new DateTime(2028, 12, 31);

        var dto = await _appService.UpdateAsync(contract.Id, snapshot);

        // Changed fields take new values.
        dto.Title.ShouldBe("更新后标题");
        dto.PartyBName.ShouldBe("新公司");
        dto.TotalAmount.ShouldBe(2_000_000m);
        dto.ExpirationDate.ShouldBe(new DateTime(2028, 12, 31));

        // Fields NOT in the user's correction must be preserved (full-snapshot semantics
        // round-trips the unchanged values rather than nulling them out).
        dto.PartyAName.ShouldBe(before.PartyAName);
        dto.ContractNumber.ShouldBe(before.ContractNumber);
        dto.SignedDate.ShouldBe(before.SignedDate);
        dto.EffectiveDate.ShouldBe(before.EffectiveDate);
        dto.Currency.ShouldBe(before.Currency);

        dto.NeedsReview.ShouldBeFalse();
        dto.ExtractionConfidence.ShouldBe(1.0);
        dto.ReviewStatus.ShouldBe(ContractReviewStatus.Corrected);
    }

    [Fact]
    public async Task Should_NoOp_When_Update_Submits_Identical_Values()
    {
        // Pending review (NeedsReview = true) so Corrected would be observable if we wrongly flipped it.
        var contract = await CreateAndSaveAsync("待审核公司", 1_500_000m, new DateTime(2027, 6, 30), needsReview: true);

        var before = await _appService.GetAsync(contract.Id);
        before.NeedsReview.ShouldBeTrue();
        before.ReviewStatus.ShouldBe(ContractReviewStatus.Pending);

        var dto = await _appService.UpdateAsync(contract.Id, ToFullSnapshot(before));

        dto.NeedsReview.ShouldBeTrue();
        dto.ReviewStatus.ShouldBe(ContractReviewStatus.Pending);
        dto.ExtractionConfidence.ShouldBe(before.ExtractionConfidence);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // ConfirmAsync
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Should_Confirm_Contract_Changes_Status_To_Active()
    {
        var contract = await CreateAndSaveAsync("待确认公司", 1_000_000m, new DateTime(2027, 3, 31), needsReview: true);

        var before = await _appService.GetAsync(contract.Id);
        before.Status.ShouldBe(ContractStatus.Draft);

        await _appService.ConfirmAsync(contract.Id);

        var after = await _appService.GetAsync(contract.Id);
        after.Status.ShouldBe(ContractStatus.Active);
    }

    [Fact]
    public async Task Should_Confirm_Sets_NeedsReview_False_And_ReviewStatus_Confirmed()
    {
        var contract = await CreateAndSaveAsync("待确认公司2", 500_000m, new DateTime(2027, 3, 31), needsReview: true);

        var before = await _appService.GetAsync(contract.Id);
        before.NeedsReview.ShouldBeTrue();
        before.ReviewStatus.ShouldBe(ContractReviewStatus.Pending);

        await _appService.ConfirmAsync(contract.Id);

        var after = await _appService.GetAsync(contract.Id);
        after.NeedsReview.ShouldBeFalse();
        after.ReviewStatus.ShouldBe(ContractReviewStatus.Confirmed);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Cross-tenant isolation
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Should_Hide_Contract_From_Other_Tenants()
    {
        var tenantA = Guid.NewGuid();
        var currentTenant = GetRequiredService<ICurrentTenant>();

        Contract contract;
        using (currentTenant.Change(tenantA))
        {
            contract = await CreateAndSaveAsync("跨租户公司", 1_000_000m, new DateTime(2027, 3, 31), false);
        }

        // Outside the Change scope, current tenant is host (null) — cannot see tenantA's contract.
        await Should.ThrowAsync<EntityNotFoundException>(async () =>
        {
            await _appService.GetAsync(contract.Id);
        });
    }

    // ────────────────────────────────────────────────────────────────────────────
    // ExportAsync
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Should_Export_Csv_With_Header_And_Row()
    {
        await CreateAndSaveAsync("CSV导出公司", 999_999m, new DateTime(2027, 12, 31), false);

        var remote = await _appService.ExportAsync(new GetContractListInput { MaxResultCount = 100 });

        remote.ShouldNotBeNull();
        remote.ContentType.ShouldBe("text/csv");

        using var reader = new StreamReader(remote.GetStream()!);
        var content = await reader.ReadToEndAsync();

        content.ShouldContain("Id,DocumentId");
        content.ShouldContain("CSV导出公司");
        content.ShouldContain("999999.00");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Helper
    // ────────────────────────────────────────────────────────────────────────────

    private static UpdateContractDto ToFullSnapshot(ContractDto dto)
    {
        return new UpdateContractDto
        {
            Title = dto.Title,
            ContractNumber = dto.ContractNumber,
            PartyAName = dto.PartyAName,
            PartyBName = dto.PartyBName,
            SignedDate = dto.SignedDate,
            EffectiveDate = dto.EffectiveDate,
            ExpirationDate = dto.ExpirationDate,
            TotalAmount = dto.TotalAmount,
            Currency = dto.Currency,
            AutoRenewal = dto.AutoRenewal,
            TerminationNoticeDays = dto.TerminationNoticeDays,
            GoverningLaw = dto.GoverningLaw,
            Summary = dto.Summary
        };
    }

    private async Task<Contract> CreateAndSaveAsync(
        string partyBName,
        decimal totalAmount,
        DateTime expirationDate,
        bool needsReview,
        Guid? documentId = null)
    {
        // Fresh AI extraction always lands as Pending+Draft. Tests that want a pre-confirmed
        // contract (needsReview: false) drive the aggregate through Confirm() so the helper
        // mirrors a real-world transition rather than fabricating an impossible state.
        var contract = await _contractManager.CreateAsync(
            documentId ?? Guid.NewGuid(),
            PaperbaseContractsDocumentTypes.General,
            new ContractFields
            {
                Title = $"{partyBName}的合同",
                ContractNumber = $"CNT-{Guid.NewGuid():N}".Substring(0, 16),
                PartyAName = "我方公司",
                PartyBName = partyBName,
                SignedDate = new DateTime(2026, 1, 1),
                EffectiveDate = new DateTime(2026, 1, 1),
                ExpirationDate = expirationDate,
                TotalAmount = totalAmount,
                Currency = "CNY",
                ExtractionConfidence = 0.95
            });

        if (!needsReview)
        {
            contract.Confirm();
        }

        await WithUnitOfWorkAsync(async () =>
        {
            await _contractRepository.InsertAsync(contract, autoSave: true);
        });

        return contract;
    }
}
