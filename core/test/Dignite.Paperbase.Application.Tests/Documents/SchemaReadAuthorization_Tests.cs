using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.DocumentTypes;
using Dignite.Paperbase.Documents.Fields;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 控制权限授予的测试用授权服务：按 policy 名（= permission 名）逐一放行。实现 <see cref="IAbpAuthorizationService"/>，
/// 因为 ABP 的 <c>IsGrantedAsync</c> / <c>CheckAsync</c> 扩展会把 <see cref="IAuthorizationService"/> cast 成它。
/// 所有 string / requirements 重载都路由到同一授予集合，不依赖框架内部路由细节。
/// </summary>
public sealed class GrantSetAuthorizationService : IAbpAuthorizationService
{
    public HashSet<string> Granted { get; set; } = new();

    // 测试桩：扩展方法（IsGrantedAsync / CheckAsync）只走 AuthorizeAsync，不读这两个成员。
    public ClaimsPrincipal CurrentPrincipal => null!;
    public IServiceProvider ServiceProvider => null!;

    // IAbpAuthorizationService（扩展方法实际走这两个 2-arg 重载）
    public Task<AuthorizationResult> AuthorizeAsync(object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        => Task.FromResult(Evaluate(requirements));

    public Task<AuthorizationResult> AuthorizeAsync(object? resource, string policyName)
        => Task.FromResult(Evaluate(policyName));

    // IAuthorizationService
    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        => Task.FromResult(Evaluate(requirements));

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        => Task.FromResult(Evaluate(policyName));

    private AuthorizationResult Evaluate(string policyName)
        => Granted.Contains(policyName) ? AuthorizationResult.Success() : AuthorizationResult.Failed();

    private AuthorizationResult Evaluate(IEnumerable<IAuthorizationRequirement> requirements)
    {
        foreach (var requirement in requirements)
        {
            if (requirement is PermissionRequirement permission && Granted.Contains(permission.PermissionName))
            {
                return AuthorizationResult.Success();
            }
        }

        return AuthorizationResult.Failed();
    }
}

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class SchemaReadAuthorizationTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 用可控的授予集合替换 always-allow 的 IAuthorizationService——这是 GetVisibleAsync /
        // 活跃字段 GetListAsync 的 programmatic OR 门、以及回收站 CheckPolicyAsync 的唯一判定来源。
        var authorizationService = new GrantSetAuthorizationService();
        context.Services.AddSingleton(authorizationService);
        context.Services.RemoveAll<IAuthorizationService>();
        context.Services.RemoveAll<IAbpAuthorizationService>();
        context.Services.AddSingleton<IAuthorizationService>(authorizationService);
        context.Services.AddSingleton<IAbpAuthorizationService>(authorizationService);

        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
    }
}

/// <summary>
/// #223：读 schema 与管理 schema 解耦。读路径（GetVisibleAsync / 活跃字段 GetListAsync）放宽为
/// <c>Documents.Default</c> 或对应 schema-admin 权限任一即可；回收站读路径仍仅 schema-admin 可达。
/// </summary>
public class SchemaReadAuthorization_Tests : PaperbaseApplicationTestBase<SchemaReadAuthorizationTestModule>
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly GrantSetAuthorizationService _authorization;

    public SchemaReadAuthorization_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _authorization = GetRequiredService<GrantSetAuthorizationService>();
    }

    private void Grant(params string[] permissions) => _authorization.Granted = new HashSet<string>(permissions);

    // ---- DocumentType 读：GetVisibleAsync ----

    [Fact]
    public async Task GetVisibleAsync_Throws_When_Neither_Documents_Nor_DocumentTypes_Granted()
    {
        Grant(/* nothing */);

        await Should.ThrowAsync<AbpAuthorizationException>(() => _documentTypeAppService.GetVisibleAsync());
    }

    [Fact]
    public async Task GetVisibleAsync_Succeeds_For_Documents_Default_Only()
    {
        // #223 修复点：文档操作者（无 DocumentTypes.Default）也能读类型 schema 驱动筛选 / 字段列 / 分类指派。
        Grant(PaperbasePermissions.Documents.Default);
        _documentTypeRepository.GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<DocumentType> { new(Guid.NewGuid(), null, "host.general", "General") });

        var result = await _documentTypeAppService.GetVisibleAsync();

        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetVisibleAsync_Succeeds_For_DocumentTypes_Default_Only()
    {
        // schema 管理员（无 Documents.Default）读自己的管理列表不受影响。
        Grant(PaperbasePermissions.DocumentTypes.Default);
        _documentTypeRepository.GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<DocumentType> { new(Guid.NewGuid(), null, "host.general", "General") });

        var result = await _documentTypeAppService.GetVisibleAsync();

        result.Count.ShouldBe(1);
    }

    // ---- FieldDefinition 读：活跃 GetListAsync ----

    [Fact]
    public async Task FieldDefinition_GetListAsync_Active_Throws_When_Neither_Granted()
    {
        Grant(/* nothing */);

        await Should.ThrowAsync<AbpAuthorizationException>(() =>
            _fieldDefinitionAppService.GetListAsync(new GetFieldDefinitionListInput
            {
                DocumentTypeId = Guid.NewGuid(),
                OnlyDeleted = false
            }));
    }

    [Fact]
    public async Task FieldDefinition_GetListAsync_Active_Succeeds_For_Documents_Default_Only()
    {
        // #223 修复点：文档操作者读字段 schema 驱动动态字段列 / 详情字段编辑 / 导出列选择。
        Grant(PaperbasePermissions.Documents.Default);
        _fieldDefinitionRepository.GetListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var result = await _fieldDefinitionAppService.GetListAsync(new GetFieldDefinitionListInput
        {
            DocumentTypeId = Guid.NewGuid(),
            OnlyDeleted = false
        });

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task FieldDefinition_GetListAsync_Active_Succeeds_For_FieldDefinitions_Default_Only()
    {
        Grant(PaperbasePermissions.FieldDefinitions.Default);
        _fieldDefinitionRepository.GetListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var result = await _fieldDefinitionAppService.GetListAsync(new GetFieldDefinitionListInput
        {
            DocumentTypeId = Guid.NewGuid(),
            OnlyDeleted = false
        });

        result.ShouldNotBeNull();
    }

    // ---- FieldDefinition 回收站：OnlyDeleted 仍仅 schema-admin 可达 ----

    [Fact]
    public async Task FieldDefinition_GetListAsync_Deleted_Throws_For_Documents_Default_Only()
    {
        // 回收站视图保持 admin 门——Documents.Default 不能透过 OR 打开它（CheckPolicyAsync 先于查询）。
        Grant(PaperbasePermissions.Documents.Default);

        await Should.ThrowAsync<AbpAuthorizationException>(() =>
            _fieldDefinitionAppService.GetListAsync(new GetFieldDefinitionListInput
            {
                DocumentTypeId = Guid.NewGuid(),
                OnlyDeleted = true
            }));
    }
}
