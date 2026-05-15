# Dignite Paperbase

这是一个 ABP 框架的项目，遵循 `.claude/rules/` 中的 ABP 核心约定和模块模板。

## 项目组织

项目分为四个主要目录：
- **core/** - ABP 应用程序核心，遵循 abp-core.md 规则
- **modules/** - 可复用业务模块，每个模块遵循 module-template.md 的结构和虚拟方法要求
- **host/** - 单租户测试主机，仅在此配置中间件（OnApplicationInitialization）
- **docs/** - 面向开发者和使用者的操作/配置/API 文档；设计方案与架构决策走 GitHub Issues，不在 docs/ 下落地

## 架构设计

Paperbase 采用**三层分离**的模块化架构：

### 第一层：Core（基础设施与扩展契约）

`core/` 包含两个核心部分：

1. **Dignite.Paperbase.Abstractions（扩展契约层）**
   - **位置**：依赖拓扑的最底层（无其他 Paperbase 项目依赖）
   - **职责**：参考 `Volo.Abp.Users.Abstractions` 模式，提供业务模块和能力模块接入平台所必需的契约
   - **内容**：
     - 文档类型注册：`DocumentTypeDefinition`、`DocumentTypeOptions`
     - 集成事件：`DocumentClassifiedEto`
     - 文本提取契约（多 OCR Provider 可插拔）：`ITextExtractor`、`TextExtractionContext`、`TextExtractionResult`
   - **约束**：不依赖任何其他 Paperbase 项目，仅依赖 ABP 基础模块

2. **Dignite.Paperbase 核心模块栈**
   - **Domain.Shared / Domain / Application / EntityFrameworkCore / HttpApi**：标准 ABP 分层
   - **核心 AI 能力（分类 / 向量-RAG / 关系推断 / Chat 问答）直接在 Application 层落地**——通过 Microsoft Agent Framework (MAF) 1.0 的 `ChatClientAgent` 实现，不再独立成 AI 模块
   - **文本提取能力栈（三层契约 + 多 Provider）**：
     - **`Dignite.Paperbase.TextExtraction`**——orchestrator + 默认 `ITextExtractor` 实现（`DefaultTextExtractor`：按文件扩展名 dispatch，图片走 OCR；其他走 Markdown Provider，PDF 无文本层时 fallback OCR）。同项目内声明 `IMarkdownTextProvider` 副契约。
     - **`Dignite.Paperbase.Ocr`**——OCR Provider 实现侧的最小契约层（`IOcrProvider` / `OcrOptions` / `OcrResult`，Markdown-first 强约束）。第三方 OCR 接入只引用此项目，看不到 orchestrator 或 IMarkdownTextProvider 副契约。
     - **OCR Provider 实现**：`Dignite.Paperbase.Ocr.PaddleOcr`（**Host 当前默认**，本地 sidecar，PP-StructureV3 走 CPU 即可，输出 Markdown）与 `Dignite.Paperbase.Ocr.AzureDocumentIntelligence`（云方案，高精度）；Host 二选一启用，切换时 `[DependsOn]` + `.csproj ProjectReference` 两处同步。
     - **Markdown Provider 实现**：`Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown`（基于 ElBruno.MarkItDotNet，覆盖 PDF/Word/HTML/纯文本/CSV/RTF/EPUB 等数字版文档）。
     - **与 OCR Provider 的不对称是故意的**——Markdown Provider 与 orchestrator 耦合度高（主要是文档格式转换器），契约 + 实现都靠近 TextExtraction；OCR Provider 第三方实现概率更高（云服务多家、本地 sidecar 多种），独立薄契约层 `Dignite.Paperbase.Ocr` 给它稳定边界。

### 第二层：Modules（业务模块生态）

`modules/` 中的各业务模块（如 Contracts）：

- **依赖关系**：依赖 `Abstractions`（注册类型、订阅事件）+ ABP 基础模块；如需调用 LLM，直接 NuGet 引用 `Microsoft.Extensions.AI` + `Microsoft.Agents.AI`
- **职责**：
  - 通过 `DocumentTypeOptions` 注册自己关心的文档类型
  - 通过 `IDistributedEventBus` 订阅 `DocumentClassifiedEto` 事件
  - **使用 MAF `ChatClientAgent` + 结构化输出**自实现领域专属的字段提取（不再有通用的 `IFieldExtractor` 抽象）
  - 持久化自己的领域聚合根，提供业务 API 和 UI
  - 在自己的聚合根上维护业务查询字段，**不得回写到 Document 聚合根**（判断依据：如果一个字段的含义只有在特定业务场景下才成立，它就不属于 `Document`）
  - 聚合根上**必须**有 `public Guid DocumentId { get; private set; }` 字段（非可空），作为指向源文档的唯一关联键；**不引入** `IHasDocumentId` 等标记接口——当前无运行时多态需求，约束通过本文件 + 结构测试双重守护
- **非耦合实现**：业务模块之间无依赖；业务模块与核心通过事件解耦通信
- **业务记录是 Document 投影，无独立删除入口**：业务模块聚合根（如 `Contract`）的初始字段全部派生自 `Document.Markdown`，可以叠加**人工修正**（"人在回路"模式：Update API + `ReviewStatus.Corrected` 状态）。Document 仍是 truth source 的源头，业务记录是它的"派生 + 修正叠加"投影。因此：
  - 业务模块**不提供**自身的 `DeleteAsync` / `PermanentDeleteAsync` API，也**不在自己的 UI 上**暴露删除/恢复/彻底删除按钮
  - 业务记录的销毁通过**删除源文档**触发：`DocumentDeletedEto` → 业务记录归档；`DocumentPermanentlyDeletedEto` → 业务记录物理删除（**人工修正一并丢失**——这是用户彻底删 Document 的明示意图）
  - 业务模块 UI 详情页**提供"打开源文档"链接**，让用户跳转到 Document 详情页执行删除
  - **Document 彻底删除的二次确认必须显式警告用户**："包括从中派生的所有业务记录及其人工修正"
  - 后端依赖拓扑也禁止业务模块调用 Core 的 `IDocumentAppService`（业务模块只能发布/订阅事件）
  - **不要为"保住人工修正"而分裂聚合根**（如假想中的 `ContractAnnotation`）——这会引入显著复杂度但收益小：用户主动彻底删源文档时，连带派生数据丢失是符合预期的，而 UI 二次确认已经把后果讲清楚了

### 第三层：Host（宿主应用）

`host/` 仅作为容器：

- 在 `[DependsOn(...)]` 中声明依赖的核心模块、能力模块和业务模块
- 在 `ConfigureServices()` 中：
  - 配置 OCR Provider（默认 PaddleOCR，可切 Azure Document Intelligence）+ Markdown Provider（ElBruno MarkItDown）
  - 注册 `IChatClient` + `IEmbeddingGenerator<string, Embedding<float>>`（Azure OpenAI 或 Ollama）——所有上层 MAF Agent 共享这套 LLM 接入
- **仅在此处配置中间件**（`OnApplicationInitialization`）
- 不实现任何业务逻辑

### 依赖流向

```
Host Application
    ├── 注册 IChatClient + IEmbeddingGenerator
    └── DependsOn:
        ├── Dignite.Paperbase.Application（核心 + 内嵌 MAF Workflow）
        ├── Dignite.Paperbase.TextExtraction（orchestrator + IMarkdownTextProvider 契约）
        ├── Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown（Markdown Provider 实现）
        ├── Dignite.Paperbase.Ocr.PaddleOcr（OCR Provider，当前默认；可切换 Ocr.AzureDocumentIntelligence）
        └── Dignite.Paperbase.Contracts.Application（业务模块）

Dignite.Paperbase.Abstractions（扩展契约层，无其他项目依赖）
    ├── DocumentTypeDefinition / DocumentTypeOptions / DocumentClassifiedEto
    ├── ITextExtractor + TextExtractionContext / TextExtractionResult（顶层 orchestrator 契约）
    └── 被业务模块订阅事件、注册类型

Dignite.Paperbase.Ocr（OCR Provider 实现侧最小契约层，无其他 Paperbase 项目依赖）
    ├── IOcrProvider + OcrOptions + OcrResult（Markdown-first）
    └── 被 Ocr.PaddleOcr / Ocr.AzureDocumentIntelligence 等 Provider 实现项目引用
```

**核心约束**：
- **单向依赖**：Abstractions 处于最底层，被所有上层引用
- **业务模块间无耦合**：每个业务模块独立开发、独立测试、可独立卸载
- **编排在 Application**：BackgroundJob、Workflow、PipelineRun 生命周期、Document 读写均在 Paperbase.Application

### Markdown-first 数据流（强制）

项目定位是 **AI 驱动的企业档案平台**，遇到取舍时优先 AI 友好的设计。Markdown 是 AI pipeline 的**唯一文本载荷**：

- **OCR / 数字版抽取**：`ITextExtractor` / `IMarkdownTextProvider` / `IOcrProvider` 实现方**必须**输出 Markdown，**不得**退回 plain text 路径。
  - **对结构化文档而言**（合同 / 政策 / 报告 / CSV / 有标题的 DOCX / PP-StructureV3 / Azure DI prebuilt-document）——标题、表格、列表是向量化切块和 LLM 理解的**真信号**，全力利用。
  - **对无结构内容而言**（OCR 散段落 / 纯 txt / PP-OCRv4 行级输出 / 单句便签）——Markdown 是**容器命名**，**不是**信号增益；保留 Markdown 路径只是为了下游 chunker / prompt / chat 消费一种格式。诚实承认这一点，不要把扁平段落包装成"也是 Markdown 信号"。
  - **翻译职责在 Provider 内部完成**——`OcrResult` / `TextExtractionResult` 不暴露 RawText 字段，Provider 拿到底层服务的纯文本输出后**自己**负责包成扁平 Markdown（例如 `string.Join("\n\n", paragraphs)`），不允许把 plain-text-to-markdown 的兜底逻辑泄漏给上游 orchestrator。
- **持久化**：`Document.Markdown` 是 Document 聚合根上唯一的文本字段，**禁止**在 `Document` 或事件载荷（如 `DocumentClassifiedEto`）上引入并行的 plain-text 字段。
- **下游消费**：向量化（`TextChunker` 按 Markdown AST 切块 + 注入 header path）、LLM 分类 / QA / Rerank、业务模块字段抽取，统一消费 Markdown。
- **纯文本投影**：仅在消费侧（如关键字兜底分类器）按需通过 `Dignite.Paperbase.Documents.MarkdownStripper.Strip(...)` 即时计算，**不持久化**也**不在契约上并列暴露**。
- **Prompt 表达**：`DefaultPromptProvider` 的系统提示词显式告知 LLM"输入是 Markdown"，让模型把结构标记当作语义信号利用，而非字面字符。

**Markdown-first 是工程默认，不是哲学原则。** Markdown 是文本载荷，但 **out-of-band 信号**（坐标 / 置信度 / page metadata / 表单 key-value 结构 / 印章位置）与 Markdown **正交**。未来若需 page-aware citations、签章定位、表单 key-value 抽取，应作为 `TextExtractionResult` 上**具名可选独立扩展字段**（例如 `IReadOnlyList<PageBlock>? PageBlocks`，可空、与 Markdown 不耦合），或独立 extractor 接口（与 `ITextExtractor` 正交）——不被"Markdown 是唯一文本载荷"的字面理解挡掉。

- **禁用模式**：在 `TextExtractionResult` 上加 `Dictionary<string, object>` / `Dictionary<string, string>` 类型的**通用"扩展槽"**——这是 code smell，未来类型不清、消费侧 cast 满天飞、对 LLM-facing schema 不友好。
- **正确做法**：每加一种 out-of-band 信号**单独开 Issue 讨论**（属架构决策），按需加**具名、强类型、可空**的字段；如果该信号与 OCR 强相关而与 Markdown Provider 无关，考虑加在 `OcrResult` 而非 `TextExtractionResult` 上以避免责任错位。

**Document 字段扩展判定**：上述原则在 transient transport（`TextExtractionResult` / `OcrResult`）层级，到 `Document` 聚合根（持久化层、跨业务模块共享的 truth source）规则更严。两轴判定：

1. **文本类型字段：永远只有 `Markdown` 一个。** 这是 Markdown-first 在持久化层的硬约束（已被 `Document.SetMarkdown` 的 immutability 强保护在代码层面执行）。任何派生文本（Summary / Outline / SectionsJson）走 `MarkdownStripper.Strip` 或切块器在消费侧投影，**不持久化**。`Title` 是 Markdown 派生的展示快照（不可变），不是新文本载荷；`ClassificationReason` 是 AI 决策解释（不是文档内容）。
2. **非文本类型字段：按"跨业务模块共享 vs 业务专属"判定**（沿用下文"业务记录是 Document 投影"段的判据——"如果一个字段的含义只有在特定业务场景下才成立，它就不属于 `Document`"）：
   - **跨业务共享、属 truth source**（如 `PageBlocks` 用于任何业务的 citation 高亮、OCR Provider name/version 用于调试）→ 可加到 `Document`，仍需开 Issue 讨论形状。
   - **业务专属**（身份证 `Name`/`IdNumber`、合同 `Amount`/`Party`、发票 `Items[]`）→ 加到业务模块的聚合根（`IdCardRecord` / `Contract` / `Invoice`），**`Document` 不污染**。

这条规则同时回答了"OCR out-of-band 信号该放哪里"——它既不属于业务模块（与具体业务无关）、也不能塞回 Markdown 字符串（破坏 Markdown-first）。它该在 `Document` 层面承载，但每加一种**单独开 Issue**，按需加具名强类型可选字段，**禁止** `Dictionary<string, object>` 通用扩展槽。

### AI 实现约定

- **Chat 路径的诚实信号**：`ChatAppService` 走在线请求，检索通过 MAF `AIFunction`（`search_paperbase_documents`）由模型 `ChatToolMode.Auto` 自决调用；模型未调用 search 工具时 `ChatTurnResultDto.IsDegraded = true` 是**诚实信号**，不做强制注入兜底；**不保留 FullText 降级**——未向量化文档由上游流水线保证最终被向量化。
- **AI 配置两节正交**：`PaperbaseAI`（host 装配 `IChatClient`，含凭据）与 `PaperbaseAIBehavior`（Application 层行为参数）**职责正交不可合并**——前者是 provider wiring，后者是行为参数，混合会让 host 看到不该看的行为开关、Application 看到不该看的凭据。
- **业务模块向 Chat 贡献能力直接用 MAF Agent Skills**（[agentskills.io open spec](https://agentskills.io)）：继承 `AgentClassSkill<TSelf>`（来自 `Microsoft.Agents.AI`），加 `[ExposeServices(typeof(AgentSkill))]` + `ITransientDependency` 让 ABP 自动注册（实例由 `IEnumerable<AgentSkill>` 在 `ChatAppService` 里被 `AgentSkillsProvider` 聚合）。每个 `[AgentSkillScript]` 方法体内**显式** `IAuthorizationService.CheckAsync(...)` + **显式** `TenantId` 谓词（不依赖 ambient `DataFilter`）+ 结果集硬上限（`Take(N)`），不得裸跑 raw SQL；用户派生自由文本字段（`title` / `partyName` / `summary` 等）经 `PromptBoundary.WrapField(...)` 包裹后再进 JSON 返回值。Paperbase**不**自造 contributor 抽象——business modules 直接消费 MAF 原语。反例见 `.claude/rules/doc-chat-anti-patterns.md` 反例 C，参照 `modules/contracts/src/Dignite.Paperbase.Contracts.Application/Chat/ContractsSkill.cs`。
- **Tool vs Skill 判据**：把一个能力注册成 MAF Skill（走 `AgentSkillsProvider`），还是注册成普通 AIFunction（直接挂 `ChatOptions.Tools`），按下面三条判：
  - **频率**：每轮几乎都用 → Tool（如 `search_paperbase_documents`），避免每轮多一次 `load_skill` 来回；只在某些意图下才用 → Skill。
  - **"何时使用"的说明长度**：单凭参数 schema 让模型自己判断 → Tool；需要多段 prose 教模型 chaining / fallback / 不应该使用的场景 → Skill。
  - **相关操作的聚类**：单一原子操作 → Tool；同领域多个操作（如 contracts 的 search/get-detail/aggregate）共享一份 instructions → Skill。
  > 灰区遇到时优先 Skill：progressive disclosure 让 advertise 成本固定（~100 tokens/skill），不会因为多放 instructions 撑爆 prompt。改判一个能力的归属属于架构决策，先开 Issue。
- **Skill 命名 / 解析 / 共享工具名约定**：
  - **每个 skill 一个 `AgentClassSkill<T>`**，绑定一个 domain（同 aggregate root / 同权限 / 同 chaining 模式）；该领域的多种操作作为不同 `[AgentSkillScript("...")]` 方法挂在一个类上（合同模块的 `ContractsSkill` 三脚本是范例）。**不要每个 script 单开一个 skill 类**——advertise 开销 × N 且不利于 LLM 选择。
  - **DI 解析只走 `IEnumerable<AgentSkill>`**，不要按具体类型 inject。`[ExposeServices(typeof(AgentSkill))]` 默认 `IncludeSelf = false`，`GetRequiredService<ContractsSkill>()` 会抛——是有意的。
  - **跨模块 LLM-facing 标识符走 `Dignite.Paperbase.Abstractions.Chat.ChatToolNames`**：业务模块需要在 SKILL.md `Instructions` 里 reference 核心工具/技能名（典型如 "fall back to `search_paperbase_documents` on empty"）时，**不要硬编码字符串**——把常量从 `ChatToolNames` 中通过 `$$"""... {{ChatToolNames.SearchPaperbaseDocuments}} ..."""` raw-interpolated 字符串里读出。`ChatToolNames` 故意住 `Abstractions/Chat/` 而不是 `Domain.Shared/Chat/ChatConsts`——后者是 DB schema 常量层，业务模块按 ABP 模块边界不能 reference 它；前者业务模块本来就 reference 着。core 重命名工具时直接改常量值，所有 skill 的 prose 编译期同步更新。

## 处理规则

1. 在 core 和 modules 中开发时，严格遵循 `.claude/rules/` 中的规则
   - 修改 ABP BackgroundJob / JobArgs 时必须读取 `.claude/rules/background-jobs.md`
2. 开发可复用模块时，**所有公共和受保护方法必须是虚拟（virtual）的**
3. 模块中不要配置中间件，仅在 host 中配置
4. 遵循 ABP 的依赖注入约定，不要手动调用 AddScoped/AddTransient/AddSingleton
5. **改动前先判断是否需要 Issue**：涉及架构决策、影响模块边界、或属于 Slice 任务的改动，**先停下，告知用户开 GitHub Issue 后再动手**；纯实现细节的 fix（如 bug fix、措辞修正）直接用 commit message 记录即可
6. **分析必须果断**：给结论时先抛**判定**再给**理由**，不要列"可能 A / 也许 B / 取决于你"的菜单把判断推回给用户。两条路都可行时，按项目既定偏好（AI-first、不重复造轮、瞄准当下与未来）选一条并说明取舍；只有真正无法靠 `grep` / `Read` 在 30 秒内自查的才允许保留不确定性。禁止 hedging 词："可能"、"也许"、"取决于具体情况"、"两种都可以"——除非确实不知道
