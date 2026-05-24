using Dignite.Paperbase.Mcp.Documents;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.Paperbase;

/// <summary>
/// Paperbase 的 MCP 出口适配器（与 REST 出口 <c>HttpApi</c> 平行）。
/// 把通道文档暴露为 MCP 资源 + 检索 tool，供 Claude Desktop / Cursor / 任意 MCP 客户端消费。
/// MCP SDK 依赖只进本项目，不渗入 Application；端点映射（<c>MapMcp</c>）仍只在 host。
/// 认证复用 host 现有 OpenIddict Bearer（端点上 RequireAuthorization）；订阅 + lifecycle 通知是后续增量（#197）。
/// </summary>
[DependsOn(
    typeof(PaperbaseApplicationModule))]
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
            .WithTools<DocumentSearchTool>();
    }
}
