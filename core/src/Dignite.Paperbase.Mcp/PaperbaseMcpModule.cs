using System.Linq;
using Dignite.Paperbase.Documents.DocumentTypes;
using Dignite.Paperbase.Mcp.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Volo.Abp.Modularity;

namespace Dignite.Paperbase;

/// <summary>
/// Paperbase 的 MCP 出口适配器（与 REST 出口 <c>HttpApi</c> 平行）。
/// 把通道文档暴露为 MCP 资源 + 检索 tool，供 Claude Desktop / Cursor / 任意 MCP 客户端消费。
/// MCP SDK 依赖只进本项目，不渗入 Application；端点映射（<c>MapMcp</c>）仍只在 host。
/// 认证复用 host 现有 OpenIddict Bearer（端点上 RequireAuthorization）；订阅 + lifecycle 通知是后续增量（#197）。
/// 出口只依赖 <c>Application.Contracts</c>（与 REST 出口对称）：所有读路径走 AppService 接口、不捅 Domain（#222）。
/// </summary>
[DependsOn(
    typeof(PaperbaseApplicationContractsModule))]
public class PaperbaseMcpModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Streamable HTTP transport；capabilities 仅声明裸 resources/tools，
        // 不声明 subscribe / listChanged —— 诚实声明 pull-only，客户端不会挂等推送（#197 再补）。
        context.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithResources<DocumentResources>()
            .WithResources<DocumentTypeResources>()
            .WithTools<DocumentSearchTool>()
            // resources/list 动态枚举当前主体可见的文档类型——AI 据此发现有哪些 documentTypeCode，
            // 再 read 各自 paperbase://document-types/{code} 拿字段 schema。文档本身不枚举（数量无限，
            // 走 search tool 发现）。read 路径仍由 DocumentTypeResources 的 UriTemplate 自动路由——
            // list 与 read 职责分离：本 handler 只填充 resources/list，template read 不受影响。
            .WithListResourcesHandler(async (ctx, ct) =>
            {
                // 委托 IDocumentTypeAppService.GetVisibleAsync：fail-closed 权限断言（方法体内 OR 断言，
                // MCP dispatch 不经 HTTP [Authorize] 但进程内 AppService 调用照常触发）+ ambient 租户隔离
                // （两层独立单层模型，只返回当前主体那一层的类型）都在 AppService 内统一执行。
                // 文档类型数量有限且应被看见，故枚举进 resources/list。
                var documentTypeAppService = ctx.Services!.GetRequiredService<IDocumentTypeAppService>();
                var types = await documentTypeAppService.GetVisibleAsync();

                return new ListResourcesResult
                {
                    Resources = types
                        .Select(t => new Resource
                        {
                            Uri = DocumentTypeResourceUri.Format(t.TypeCode),
                            Name = t.TypeCode,
                            Description = "Paperbase document type field schema.",
                            MimeType = "application/json"
                        })
                        .ToList()
                };
            });
    }
}
