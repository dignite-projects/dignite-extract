using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Contracts;
using Dignite.Paperbase.Contracts.EntityFrameworkCore;
using Dignite.Paperbase.Host.Data;
using Dignite.Paperbase.EntityFrameworkCore;
using Dignite.Paperbase.Host.HealthChecks;
using Dignite.Paperbase.Host.Localization;
using Dignite.Paperbase.Localization;
using Dignite.Paperbase.Ocr.PaddleOcr;
using Dignite.Paperbase.TextExtraction;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Qdrant.Client;
using Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using OpenAI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using OpenIddict.Validation.AspNetCore;
using Volo.Abp;
using Volo.Abp.Account;
using Volo.Abp.Account.Web;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.Localization;
using Volo.Abp.AspNetCore.Mvc.UI.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.LeptonXLite;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.LeptonXLite.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Shared;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.AuditLogging.EntityFrameworkCore;
using Volo.Abp.Autofac;
using Volo.Abp.BackgroundJobs.EntityFrameworkCore;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.FileSystem;
using Volo.Abp.Caching;
using Volo.Abp.Emailing;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.SqlServer;
using Volo.Abp.FeatureManagement;
using Volo.Abp.FeatureManagement.EntityFrameworkCore;
using Volo.Abp.Identity;
using Volo.Abp.Identity.EntityFrameworkCore;
using Volo.Abp.Localization;
using Volo.Abp.Mapperly;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.OpenIddict;
using Volo.Abp.OpenIddict.EntityFrameworkCore;
using Volo.Abp.PermissionManagement;
using Volo.Abp.PermissionManagement.EntityFrameworkCore;
using Volo.Abp.PermissionManagement.HttpApi;
using Volo.Abp.PermissionManagement.Identity;
using Volo.Abp.PermissionManagement.OpenIddict;
using Volo.Abp.Security.Claims;
using Volo.Abp.SettingManagement;
using Volo.Abp.SettingManagement.EntityFrameworkCore;
using Volo.Abp.Studio;
using Volo.Abp.Studio.Client.AspNetCore;
using Volo.Abp.Swashbuckle;
using Volo.Abp.Timing;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Paperbase.Host;

[DependsOn(
    // ABP Framework packages
    typeof(AbpAspNetCoreMvcModule),
    typeof(AbpAutofacModule),
    typeof(AbpMapperlyModule),
    typeof(AbpCachingModule),
    typeof(AbpSwashbuckleModule),
    typeof(AbpAspNetCoreSerilogModule),
    typeof(AbpStudioClientAspNetCoreModule),

    // theme
    typeof(AbpAspNetCoreMvcUiLeptonXLiteThemeModule),

    // Account module packages
    typeof(AbpAccountApplicationModule),
    typeof(AbpAccountHttpApiModule),
    typeof(AbpAccountWebOpenIddictModule),

    // Identity module packages
    typeof(AbpPermissionManagementDomainIdentityModule),
    typeof(AbpPermissionManagementDomainOpenIddictModule),
    typeof(AbpIdentityApplicationModule),
    typeof(AbpIdentityHttpApiModule),

    // Permission Management module packages
    typeof(AbpPermissionManagementApplicationModule),
    typeof(AbpPermissionManagementHttpApiModule),

    // Feature Management module packages
    typeof(AbpFeatureManagementHttpApiModule),
    typeof(AbpFeatureManagementApplicationModule),

    // Setting Management module packages
    typeof(AbpSettingManagementHttpApiModule),
    typeof(AbpSettingManagementApplicationModule),

    // Entity Framework Core packages for the used modules
    typeof(AbpAuditLoggingEntityFrameworkCoreModule),
    typeof(AbpIdentityEntityFrameworkCoreModule),
    typeof(AbpOpenIddictEntityFrameworkCoreModule),
    typeof(AbpFeatureManagementEntityFrameworkCoreModule),
    typeof(AbpPermissionManagementEntityFrameworkCoreModule),
    typeof(AbpSettingManagementEntityFrameworkCoreModule),
    typeof(AbpBackgroundJobsEntityFrameworkCoreModule),
    typeof(AbpBlobStoringFileSystemModule),
    typeof(AbpEntityFrameworkCoreSqlServerModule),

    // Paperbase core modules
    typeof(PaperbaseHttpApiModule),
    typeof(PaperbaseApplicationModule),
    typeof(PaperbaseEntityFrameworkCoreModule),

    // Paperbase infrastructure modules
    typeof(PaperbaseTextExtractionModule),
    typeof(PaperbaseTextExtractionElBrunoMarkItDownModule),
    typeof(PaperbasePaddleOcrModule),                  // 默认 OCR Provider（本地 sidecar，PP-StructureV3 走 CPU 即可，输出 Markdown）
    // typeof(PaperbaseAzureDocumentIntelligenceModule), // 云方案（高精度），切换时同步在 .csproj 注释 / 启用 ProjectReference

    // Paperbase business modules
    typeof(PaperbaseContractsHttpApiModule),
    typeof(PaperbaseContractsApplicationModule),
    typeof(PaperbaseContractsEntityFrameworkCoreModule)
)]
public class PaperbaseHostModule : AbpModule
{
    /* Single point to enable/disable multi-tenancy */
    public const bool IsMultiTenant = false;

    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        context.Services.PreConfigure<AbpMvcDataAnnotationsLocalizationOptions>(options =>
        {
            options.AddAssemblyResource(
                typeof(PaperbaseHostResource)
            );
        });

        PreConfigure<OpenIddictBuilder>(builder =>
        {
            builder.AddValidation(options =>
            {
                options.AddAudiences("Paperbase");
                options.UseLocalServer();
                options.UseAspNetCore();
            });
        });

        if (!hostingEnvironment.IsDevelopment())
        {
            PreConfigure<AbpOpenIddictAspNetCoreOptions>(options =>
            {
                options.AddDevelopmentEncryptionAndSigningCertificate = false;
            });

            PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
            {
                serverBuilder.AddProductionEncryptionAndSigningCertificate("openiddict.pfx", configuration["AuthServer:CertificatePassPhrase"]!);
            });
        }

        PaperbaseHostGlobalFeatureConfigurator.Configure();
        PaperbaseHostModuleExtensionConfigurator.Configure();
        PaperbaseHostEfCoreEntityExtensionMappings.Configure();
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        if (!hostingEnvironment.IsProduction())
        {
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.LogCompleteSecurityArtifact = true;
        }

        if (hostingEnvironment.IsDevelopment())
        {
            context.Services.Replace(ServiceDescriptor.Singleton<IEmailSender, NullEmailSender>());
        }

        Configure<AbpClockOptions>(options =>
        {
            options.Kind = DateTimeKind.Utc;
        });

        ConfigureStudio(hostingEnvironment);
        ConfigureAuthentication(context);
        ConfigureBundles(hostingEnvironment);
        ConfigureMultiTenancy();
        ConfigureUrls(configuration);
        ConfigureHealthChecks(context);
        ConfigureSwagger(context.Services, configuration);
        ConfigureAutoApiControllers();
        ConfigureLocalization();
        ConfigureCors(context, configuration);
        ConfigureDataProtection(context);
        ConfigureVirtualFiles(hostingEnvironment);
        ConfigureEfCore(context);
        ConfigureAI(context, configuration);
        ConfigureVectorStore(context, configuration);
        ConfigureOpenTelemetry(context, configuration);
    }

    // Register Microsoft.Extensions.VectorData's Qdrant connector. Connection params come
    // from PaperbaseVectorStore:Qdrant:* (host-only — IChatClient and IEmbeddingGenerator are
    // wired separately in ConfigureAI). Chat-side options like CollectionName /
    // EmbeddingDimension / MinScore live on PaperbaseVectorStoreOptions and are bound in
    // PaperbaseApplicationModule.
    private void ConfigureVectorStore(ServiceConfigurationContext context, IConfiguration configuration)
    {
        context.Services.AddSingleton<QdrantClient>(_ =>
        {
            var endpoint = configuration["PaperbaseVectorStore:Qdrant:Endpoint"] ?? "http://localhost:6334";
            var apiKey = configuration["PaperbaseVectorStore:Qdrant:ApiKey"];
            var uri = new Uri(endpoint);
            return new QdrantClient(
                host: uri.Host,
                port: uri.Port > 0 ? uri.Port : 6334,
                https: string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase),
                apiKey: string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
        });

        context.Services.AddQdrantVectorStore();
    }
    

    private void ConfigureHealthChecks(ServiceConfigurationContext context)
    {
        context.Services.AddPaperbaseHealthChecks();
    }
    
    private void ConfigureStudio(IHostEnvironment hostingEnvironment)
    {
        if (hostingEnvironment.IsProduction())
        {
            Configure<AbpStudioClientOptions>(options =>
            {
                options.IsLinkEnabled = false;
            });
        }
    }

    private void ConfigureAuthentication(ServiceConfigurationContext context)
    {
        context.Services.ForwardIdentityAuthenticationForBearer(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        context.Services.Configure<AbpClaimsPrincipalFactoryOptions>(options =>
        {
            options.IsDynamicClaimsEnabled = true;
        });
    }

    private void ConfigureBundles(IHostEnvironment hostingEnvironment)
    {
        Configure<AbpBundlingOptions>(options =>
        {
            options.StyleBundles.Configure(
                LeptonXLiteThemeBundles.Styles.Global,
                bundle => 
                { 
                    bundle.AddFiles("/global-styles.css"); 
                }
            );

            options.ScriptBundles.Configure(
                LeptonXLiteThemeBundles.Scripts.Global,
                bundle =>
                {
                    bundle.AddFiles("/global-scripts.js");
                    if (hostingEnvironment.IsDevelopment())
                    {
                        bundle.AddFiles("/dev-login-helper.js");
                    }
                }
            );
        });
    }

    private void ConfigureMultiTenancy()
    {
        Configure<AbpMultiTenancyOptions>(options =>
        {
            options.IsEnabled = IsMultiTenant;
        });
    }

    private void ConfigureUrls(IConfiguration configuration)
    {
        Configure<AppUrlOptions>(options =>
        {
            options.Applications["MVC"].RootUrl = configuration["App:SelfUrl"];
            options.RedirectAllowedUrls.AddRange(configuration["App:RedirectAllowedUrls"]?.Split(',') ?? Array.Empty<string>());

            options.Applications["Angular"].RootUrl = configuration["App:ClientUrl"];
            options.Applications["Angular"].Urls[AccountUrlNames.PasswordReset] = "account/reset-password";
        });
    }

    private void ConfigureLocalization()
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Add<PaperbaseHostResource>("en")
                .AddBaseTypes(typeof(PaperbaseResource))
                .AddVirtualJson("/Localization/PaperbaseHost");

            options.DefaultResourceType = typeof(PaperbaseHostResource);

            options.Languages.Add(new LanguageInfo("en", "en", "English"));
            options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "Chinese (Simplified)"));
            options.Languages.Add(new LanguageInfo("zh-Hant", "zh-Hant", "Chinese (Traditional)"));
            options.Languages.Add(new LanguageInfo("ja", "ja", "日语"));
        });

    }

    private void ConfigureAutoApiControllers()
    {
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers.Create(typeof(PaperbaseHostModule).Assembly);
        });
    }

    private void ConfigureSwagger(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAbpSwaggerGenWithOAuth(
            configuration["AuthServer:Authority"]!,
            new Dictionary<string, string>
            {
                {"Paperbase", "Paperbase API"}
            },
            options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo { Title = "Paperbase API", Version = "v1" });
                options.DocInclusionPredicate((docName, description) => true);
                options.CustomSchemaIds(type => type.FullName);
            });
    }

    private void ConfigureCors(ServiceConfigurationContext context, IConfiguration configuration)
    {
        context.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(builder =>
            {
                builder
                    .WithOrigins(
                        configuration["App:CorsOrigins"]?
                            .Split(",", StringSplitOptions.RemoveEmptyEntries)
                            .Select(o => o.RemovePostFix("/"))
                            .ToArray() ?? Array.Empty<string>()
                    )
                    .WithAbpExposedHeaders()
                    .SetIsOriginAllowedToAllowWildcardSubdomains()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });
    }

    private void ConfigureDataProtection(ServiceConfigurationContext context)
    {
        context.Services.AddDataProtection().SetApplicationName("Paperbase");
    }

    private void ConfigureVirtualFiles(IWebHostEnvironment hostingEnvironment)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<PaperbaseHostModule>();
            if (hostingEnvironment.IsDevelopment())
            {
                /* Using physical files in development, so we don't need to recompile on changes */
                options.FileSets.ReplaceEmbeddedByPhysical<PaperbaseHostModule>(hostingEnvironment.ContentRootPath);
            }
        });
    }

    private void ConfigureEfCore(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<Data.PaperbaseHostDbContext>(options =>
        {
            options.AddDefaultRepositories(includeAllEntities: true);
        });

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(configurationContext =>
            {
                configurationContext.UseSqlServer();
            });
        });

        var hostingEnvironment = context.Services.GetHostingEnvironment();
        Configure<AbpBlobStoringOptions>(options =>
        {
            options.Containers.ConfigureDefault(container =>
            {
                container.UseFileSystem(fileSystem =>
                {
                    fileSystem.BasePath = Path.Combine(
                        hostingEnvironment.ContentRootPath, "App_Data", "blobs");
                });
            });
        });
    }

    private void ConfigureAI(ServiceConfigurationContext context, IConfiguration configuration)
    {
        var openAIClient = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(configuration["PaperbaseAI:ApiKey"]!),
            new OpenAIClientOptions { Endpoint = new Uri(configuration["PaperbaseAI:Endpoint"]!) });

        var chatBuilder = context.Services
            .AddChatClient(_ => openAIClient
                .GetChatClient(configuration["PaperbaseAI:ChatModelId"]!)
                .AsIChatClient())
            .UseFunctionInvocation(configure: invoker =>
                invoker.MaximumIterationsPerRequest =
                    configuration.GetValue("PaperbaseAI:MaxToolIterations", 10));

        if (configuration.GetValue("PaperbaseAI:PromptCachingEnabled", defaultValue: true))
            chatBuilder = chatBuilder.UseDistributedCache();

        // OTel decorators emit the gen_ai.* semantic-convention signals (turn duration,
        // token usage, execute_tool spans). Wire them inside the pipeline so a host that
        // adds an OTel exporter automatically picks up the standard signals — Paperbase's
        // own ChatTelemetryRecorder only adds project-specific deltas on top
        // (turn.degraded counter, tool.result.size histogram, business audit log).
        chatBuilder.UseOpenTelemetry();
        chatBuilder.UseLogging();

        // Summarizer chat client: separate keyed registration consumed by
        // ChatCompactionStrategyFactory's SummarizationCompactionStrategy. Falls back to
        // the primary ChatModelId when SummarizerModelId is unset so the factory always
        // resolves a client even on hosts that don't configure compaction.
        // No UseFunctionInvocation / UseDistributedCache (single-shot summary, no tools,
        // negligible cache hit rate); OTel + Logging stay so summarizer cost is observable.
        var summarizerModelId = configuration["PaperbaseAI:SummarizerModelId"]
            ?? configuration["PaperbaseAI:ChatModelId"]!;
        context.Services.AddKeyedChatClient(
            PaperbaseAIConsts.SummarizerChatClientKey,
            _ => openAIClient.GetChatClient(summarizerModelId).AsIChatClient())
            .UseOpenTelemetry()
            .UseLogging();

        // Title-generator chat client: same shape as the summarizer — single-shot,
        // tool-free, prompt-unique-per-call so distributed caching is a net negative.
        // Consumed by ChatAppService.TryGenerateAndApplyTitleAsync and by
        // DocumentTextExtractionBackgroundJob.TryGenerateTitleAsync via
        // [FromKeyedServices(PaperbaseAIConsts.TitleGeneratorChatClientKey)]. Falls back
        // to the primary ChatModelId when TitleGeneratorModelId is unset; a host that
        // wants to cut cost can point this at a small fast model (e.g. Qwen3-8B) while
        // keeping the main chat on a stronger one.
        var titleGeneratorModelId = configuration["PaperbaseAI:TitleGeneratorModelId"]
            ?? configuration["PaperbaseAI:ChatModelId"]!;
        context.Services.AddKeyedChatClient(
            PaperbaseAIConsts.TitleGeneratorChatClientKey,
            _ => openAIClient.GetChatClient(titleGeneratorModelId).AsIChatClient())
            .UseOpenTelemetry()
            .UseLogging();

        // Structured-output chat client: shared by every single-shot RunAsync<T> caller
        // (DocumentClassificationWorkflow, DocumentRerankWorkflow,
        // ContractDocumentHandler.ExtractFieldsAsync). All three are tool-free and their
        // prompts are document-content-derived (unique per call), so FunctionInvocation
        // and DistributedCache are pure overhead. OTel + Logging stay so each structured
        // call shows up as a clean chat <model> span (no phantom orchestrate_tools wrap).
        // Falls back to ChatModelId when StructuredModelId is unset; production teams
        // running tight token budgets can point this at a smaller / cheaper model that
        // can still satisfy schema-bound output.
        var structuredModelId = configuration["PaperbaseAI:StructuredModelId"]
            ?? configuration["PaperbaseAI:ChatModelId"]!;
        context.Services.AddKeyedChatClient(
            PaperbaseAIConsts.StructuredChatClientKey,
            _ => openAIClient.GetChatClient(structuredModelId).AsIChatClient())
            .UseOpenTelemetry()
            .UseLogging();

        context.Services
            .AddEmbeddingGenerator(_ => openAIClient
                .GetEmbeddingClient(configuration["PaperbaseAI:EmbeddingModelId"]!)
                .AsIEmbeddingGenerator())
            .UseOpenTelemetry()
            .UseLogging();
    }

    // OTel export pipeline. MAF (CompactionTelemetry / Microsoft.Agents.AI), Microsoft.Extensions.AI
    // (gen_ai.* spans from the chat-client UseOpenTelemetry decorators above), and Paperbase's own
    // ChatTelemetryRecorder / RelationDiscoveryTelemetryRecorder all emit signals. Without an
    // exporter wired here they are silently dropped. See plan doc § "启发点 2：接通 OpenTelemetry
    // 导出管道" and Issue #142 for the full rationale.
    private void ConfigureOpenTelemetry(ServiceConfigurationContext context, IConfiguration configuration)
    {
        var section = configuration.GetSection("OpenTelemetry");
        if (!section.GetValue("Enabled", defaultValue: false))
        {
            return;
        }

        var endpointValue = section["Otlp:Endpoint"];
        var useOtlp = !string.IsNullOrWhiteSpace(endpointValue);
        var useConsole = section.GetValue("ConsoleExporter", defaultValue: false);

        var serviceVersion = typeof(PaperbaseHostModule).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        var otel = context.Services.AddOpenTelemetry();

        otel.ConfigureResource(resource => resource
            .AddService(serviceName: "Dignite.Paperbase", serviceVersion: serviceVersion));

        otel.WithTracing(tracing =>
        {
            tracing
                // MAF spans incl. CompactionTelemetry's compaction.compact / compaction.summarize.
                // The actual ActivitySource name MAF uses is the Experimental.* prefixed form
                // (OpenTelemetryConsts.DefaultSourceName = "Experimental.Microsoft.Agents.AI").
                // The unprefixed form is registered too in case Microsoft drops the Experimental
                // prefix in a future stable release.
                .AddSource("Experimental.Microsoft.Agents.AI")
                .AddSource("Microsoft.Agents.AI")
                // gen_ai.* spans from the chat-client / embedding-client UseOpenTelemetry
                // decorators. Same Experimental.* convention as MAF.
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddSource("Microsoft.Extensions.AI")
                // Future ActivitySources under the project's namespace (wildcard supported by OTel).
                .AddSource("Dignite.Paperbase.*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (useOtlp)
            {
                tracing.AddOtlpExporter(o => ConfigureOtlpExporter(o, section));
            }

            if (useConsole)
            {
                tracing.AddConsoleExporter();
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics
                // Paperbase-owned Meters: Dignite.Paperbase.Chat,
                // Dignite.Paperbase.Documents.RelationDiscovery, Dignite.Paperbase.Contracts,
                // plus future siblings.
                .AddMeter("Dignite.Paperbase.*")
                // MAF / ME.AI Meters — same Experimental.* prefix convention as traces.
                .AddMeter("Experimental.Microsoft.Agents.AI")
                .AddMeter("Microsoft.Agents.AI")
                .AddMeter("Experimental.Microsoft.Extensions.AI")
                .AddMeter("Microsoft.Extensions.AI")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (useOtlp)
            {
                metrics.AddOtlpExporter(o => ConfigureOtlpExporter(o, section));
            }

            if (useConsole)
            {
                metrics.AddConsoleExporter();
            }
        });
    }

    private static void ConfigureOtlpExporter(
        OpenTelemetry.Exporter.OtlpExporterOptions options,
        IConfigurationSection section)
    {
        var endpoint = section["Otlp:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint);
        }

        var protocol = section["Otlp:Protocol"];
        if (string.Equals(protocol, "HttpProtobuf", StringComparison.OrdinalIgnoreCase))
        {
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        }
        else if (string.Equals(protocol, "Grpc", StringComparison.OrdinalIgnoreCase))
        {
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
        }
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        var env = context.GetEnvironment();

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseAbpRequestLocalization();

        if (!env.IsDevelopment())
        {
            app.UseErrorPage();
        }

        app.UseCorrelationId();
        app.UseRouting();
        app.UseStaticFiles();
        app.UseAbpStudioLink();
        app.UseAbpSecurityHeaders();
        app.UseCors();
        app.UseAuthentication();
        app.UseAbpOpenIddictValidation();

        if (IsMultiTenant)
        {
            app.UseMultiTenancy();
        }

        app.UseUnitOfWork();
        app.UseDynamicClaims();
        app.UseAuthorization();

        app.UseSwagger();
        app.UseAbpSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Paperbase API");
            options.OAuthClientId(context.GetConfiguration()["AuthServer:SwaggerClientId"]);
        });

        app.UseAuditing();
        app.UseAbpSerilogEnrichers();
        app.UseConfiguredEndpoints();
    }
}
