import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  OnDestroy,
  inject,
  signal,
  computed,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import {
  DocumentDto,
  DocumentLifecycleStatus,
  DocumentPipelineRunDto,
  DocumentReviewStatus,
  DocumentService,
  FieldDataType,
  FieldDefinitionDto,
  FieldDefinitionService,
  PAPERBASE_PERMISSIONS,
  PipelineRunStatus,
} from '@dignite/paperbase';

interface PipelineRow {
  pipelineCode: string;
  labelKey: string;
  isKnown: boolean;
  run: DocumentPipelineRunDto | null;
  // Pre-computed view fields. Without these, the template re-invoked
  // getRunStatusBadgeClass / getElapsedMs / formatElapsed / isRetryable on
  // every change detection cycle for every row. Now they are derived once
  // when the pipelineRows signal recomputes (i.e. when the document is
  // (re)loaded).
  statusBadgeClass: string;
  statusLabel: string;
  inProgress: boolean;
  elapsedDisplay: string | null;
  retryable: boolean;
}

// Mirrors core/src/Dignite.Paperbase.Domain.Shared/Documents/PaperbasePipelines.cs.
const KNOWN_PIPELINE_CODES = [
  'text-extraction',
  'classification',
] as const;

@Component({
  selector: 'lib-document-detail',
  templateUrl: './document-detail.component.html',
  styleUrls: ['./document-detail.component.scss'],
  imports: [CommonModule, RouterModule, FormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentDetailComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly documentService = inject(DocumentService);
  private readonly fieldDefinitionService = inject(FieldDefinitionService);
  private readonly toaster = inject(ToasterService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canDelete = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Delete,
  );
  readonly canEditFields = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.ConfirmClassification,
  );

  document = signal<DocumentDto | null>(null);
  isLoading = signal(true);
  isTextExpanded = signal(false);
  imageError = signal(false);
  retryingPipeline = signal<string | null>(null);
  blobUrl = signal<string | null>(null);
  isBlobLoading = signal(false);
  isEditingFields = signal(false);
  editedFields = signal<Record<string, string>>({});
  isSavingFields = signal(false);
  fieldDefinitions = signal<FieldDefinitionDto[]>([]);

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewStatus = DocumentReviewStatus;
  readonly PipelineRunStatus = PipelineRunStatus;

  pipelineRows = computed<PipelineRow[]>(() => {
    const doc = this.document();
    if (!doc) return [];

    const allRuns = doc.pipelineRuns ?? [];
    const known: PipelineRow[] = KNOWN_PIPELINE_CODES.map(code => this.toPipelineRow(
      code,
      `::Document:Pipeline:${code}`,
      true,
      this.pickLatestRun(allRuns, code),
    ));

    const unknownCodes = Array.from(
      new Set(
        allRuns
          .map(r => r.pipelineCode)
          .filter(code => !!code && !KNOWN_PIPELINE_CODES.includes(code as typeof KNOWN_PIPELINE_CODES[number]))
      )
    );

    const unknown: PipelineRow[] = unknownCodes.map(code => this.toPipelineRow(
      code,
      code,
      false,
      this.pickLatestRun(allRuns, code),
    ));

    return [...known, ...unknown];
  });

  protected toPipelineRow(
    pipelineCode: string,
    labelKey: string,
    isKnown: boolean,
    run: DocumentPipelineRunDto | null,
  ): PipelineRow {
    return {
      pipelineCode,
      labelKey,
      isKnown,
      run,
      statusBadgeClass: this.getRunStatusBadgeClass(run?.status),
      statusLabel: this.getRunStatusLabel(run?.status),
      inProgress: this.isRunInProgress(run?.status),
      elapsedDisplay: run ? this.formatElapsedOrNull(run) : null,
      retryable: isKnown && this.isRetryable(run),
    };
  }

  protected formatElapsedOrNull(run: DocumentPipelineRunDto): string | null {
    return this.getElapsedMs(run) === null ? null : this.formatElapsed(run);
  }

  needsReview = computed(() =>
    this.document()?.reviewStatus === DocumentReviewStatus.PendingReview
  );

  isProcessing = computed(() => {
    if (this.needsReview()) return false;

    const status = this.document()?.lifecycleStatus;
    return status === DocumentLifecycleStatus.Uploaded ||
           status === DocumentLifecycleStatus.Processing;
  });

  isReady = computed(() =>
    this.document()?.lifecycleStatus === DocumentLifecycleStatus.Ready
  );

  isImage = computed(() =>
    this.document()?.fileOrigin?.contentType?.startsWith('image/') ?? false
  );

  // Type-bound extracted fields (field architecture v2). Key = field name; value
  // is decoded server-side from a SQL Server json column. Sorted by key for a
  // stable display order.
  extractedFieldEntries = computed<{ key: string; value: string }[]>(() => {
    const fields = this.document()?.extractedFields;
    if (!fields) return [];
    return Object.keys(fields)
      .sort((a, b) => a.localeCompare(b))
      .map(key => ({ key, value: this.formatFieldValue(fields[key]) }));
  });

  // 字段卡片显示条件：已有抽取值（只读展示），或可编辑且该类型有字段定义（支持补全空字段）。
  showFieldsCard = computed(() =>
    this.extractedFieldEntries().length > 0 ||
    (this.canEditFields && this.fieldDefinitions().length > 0)
  );

  private documentId!: string;

  ngOnInit(): void {
    this.documentId = this.route.snapshot.paramMap.get('id')!;
    this.loadDocument();
  }

  refresh(): void {
    this.loadDocument();
  }

  private loadDocument(): void {
    this.isLoading.set(true);
    this.documentService.get(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
      next: doc => {
        this.document.set(doc);
        this.isLoading.set(false);
        // 仅图片需要立即加载（内联预览）；非图片等用户点击"打开文件"时再下载
        if (doc.fileOrigin?.contentType?.startsWith('image/')) {
          this.loadBlob();
        }
        // 编辑字段需要该类型的字段定义（含 LLM 漏抽的空字段）以支持补全。
        // getByDocumentType 需 ConfirmClassification 权限，仅在可编辑时拉取避免 403。
        if (this.canEditFields && doc.documentTypeCode) {
          this.loadFieldDefinitions(doc.documentTypeCode);
        }
      },
      error: () => {
        this.isLoading.set(false);
      },
    });
  }

  private loadFieldDefinitions(typeCode: string): void {
    this.fieldDefinitionService.getByDocumentType(typeCode)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: defs => this.fieldDefinitions.set(defs),
        error: () => this.fieldDefinitions.set([]),
      });
  }

  private loadBlob(): void {
    const oldUrl = this.blobUrl();
    if (oldUrl) URL.revokeObjectURL(oldUrl);
    this.blobUrl.set(null);

    this.documentService.getBlob(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => this.blobUrl.set(URL.createObjectURL(blob)),
      });
  }

  openFile(): void {
    const existing = this.blobUrl();
    if (existing) {
      window.open(existing, '_blank');
      return;
    }
    this.isBlobLoading.set(true);
    this.documentService.getBlob(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => {
          const url = URL.createObjectURL(blob);
          this.blobUrl.set(url);
          this.isBlobLoading.set(false);
          window.open(url, '_blank');
        },
        error: () => {
          this.isBlobLoading.set(false);
        },
      });
  }

  ngOnDestroy(): void {
    const url = this.blobUrl();
    if (url) URL.revokeObjectURL(url);
  }

  onImageError(): void {
    this.imageError.set(true);
  }

  toggleText(): void {
    this.isTextExpanded.set(!this.isTextExpanded());
  }

  goBack(): void {
    this.router.navigate(['/documents']);
  }

  delete(): void {
    const doc = this.document();
    if (!doc) return;
    this.confirmation
      .warn('::Document:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.documentService.delete(doc.id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
          next: () => {
            this.toaster.success('::Document:DeletedSuccessfully', '::Success');
            this.router.navigate(['/documents']);
          },
        });
      });
  }

  getStatusBadgeClass(status: DocumentLifecycleStatus): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:   return 'badge bg-secondary';
      case DocumentLifecycleStatus.Processing: return 'badge bg-warning text-dark';
      case DocumentLifecycleStatus.Ready:      return 'badge bg-success';
      case DocumentLifecycleStatus.Failed:     return 'badge bg-danger';
      default:                                 return 'badge bg-secondary';
    }
  }

  getDocumentStatusBadgeClass(doc: DocumentDto): string {
    if (doc.reviewStatus === DocumentReviewStatus.PendingReview) {
      return 'badge bg-warning text-dark';
    }

    return this.getStatusBadgeClass(doc.lifecycleStatus);
  }

  getStatusLabel(status: DocumentLifecycleStatus): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:   return '::Document:Status:Uploaded';
      case DocumentLifecycleStatus.Processing: return '::Document:Status:Processing';
      case DocumentLifecycleStatus.Ready:      return '::Document:Status:Ready';
      case DocumentLifecycleStatus.Failed:     return '::Document:Status:Failed';
      default:                                 return '::Document:Status:Unknown';
    }
  }

  getDocumentStatusLabel(doc: DocumentDto): string {
    if (doc.reviewStatus === DocumentReviewStatus.PendingReview) {
      return '::DocumentReviewStatus:PendingReview';
    }

    return this.getStatusLabel(doc.lifecycleStatus);
  }

  getRunStatusBadgeClass(status: PipelineRunStatus | undefined): string {
    switch (status) {
      case PipelineRunStatus.Pending:   return 'badge bg-secondary';
      case PipelineRunStatus.Running:   return 'badge bg-warning text-dark';
      case PipelineRunStatus.Succeeded: return 'badge bg-success';
      case PipelineRunStatus.Failed:    return 'badge bg-danger';
      case PipelineRunStatus.Skipped:   return 'badge bg-light text-dark border';
      default:                          return 'badge bg-light text-muted border';
    }
  }

  getRunStatusLabel(status: PipelineRunStatus | undefined): string {
    switch (status) {
      case PipelineRunStatus.Pending:   return '::Document:Pipeline:Status:Pending';
      case PipelineRunStatus.Running:   return '::Document:Pipeline:Status:Running';
      case PipelineRunStatus.Succeeded: return '::Document:Pipeline:Status:Succeeded';
      case PipelineRunStatus.Failed:    return '::Document:Pipeline:Status:Failed';
      case PipelineRunStatus.Skipped:   return '::Document:Pipeline:Status:Skipped';
      default:                          return '::Document:Pipeline:Status:NotStarted';
    }
  }

  isRunInProgress(status: PipelineRunStatus | undefined): boolean {
    return status === PipelineRunStatus.Pending || status === PipelineRunStatus.Running;
  }

  isRetryable(run: DocumentPipelineRunDto | null | undefined): boolean {
    return !!run && run.status === PipelineRunStatus.Failed;
  }

  retryPipeline(pipelineCode: string): void {
    if (this.retryingPipeline() !== null) return;

    this.retryingPipeline.set(pipelineCode);
    this.documentService.retryPipeline(this.documentId, pipelineCode)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
      next: () => {
        this.retryingPipeline.set(null);
        this.toaster.success('::Document:Pipeline:RetryQueued', '::Success');
        this.loadDocument();
      },
      error: () => {
        this.retryingPipeline.set(null);
        this.toaster.error('::Document:Pipeline:RetryFailed', '::Error');
      },
    });
  }

  getElapsedMs(run: DocumentPipelineRunDto): number | null {
    if (!run.startedAt) return null;
    const start = new Date(run.startedAt).getTime();
    if (Number.isNaN(start)) return null;
    const end = run.completedAt ? new Date(run.completedAt).getTime() : Date.now();
    if (Number.isNaN(end) || end < start) return null;
    return end - start;
  }

  formatFieldValue(value: unknown): string {
    if (value === null || value === undefined) return '—';
    if (typeof value === 'object') return JSON.stringify(value);
    return String(value);
  }

  startEditFields(): void {
    const fields = this.document()?.extractedFields ?? {};
    const init: Record<string, string> = {};
    // 基于字段定义遍历——包含 LLM 漏抽的空字段，让操作员补全。
    for (const def of this.fieldDefinitions()) {
      const v = fields[def.name];
      init[def.name] = v === null || v === undefined ? '' : this.formatFieldValue(v);
    }
    this.editedFields.set(init);
    this.isEditingFields.set(true);
  }

  updateField(key: string, value: string): void {
    this.editedFields.update(f => ({ ...f, [key]: value }));
  }

  cancelEditFields(): void {
    this.isEditingFields.set(false);
    this.editedFields.set({});
  }

  saveFields(): void {
    const doc = this.document();
    if (!doc) return;
    this.isSavingFields.set(true);
    const edited = this.editedFields();
    const fields: Record<string, unknown> = {};
    for (const key of Object.keys(edited)) {
      // 空 = 不写该字段（整体替换语义下即从 ExtractedFields 移除）。
      if (edited[key].trim() === '') continue;
      fields[key] = this.coerceValue(key, edited[key]);
    }
    this.documentService.updateExtractedFields(doc.id, fields)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => {
          this.document.set(updated);
          this.isSavingFields.set(false);
          this.isEditingFields.set(false);
          this.editedFields.set({});
          this.toaster.success('::Document:FieldsUpdated', '::Success');
        },
        error: () => {
          this.isSavingFields.set(false);
          this.toaster.error('::Document:UpdateFailed', '::Error');
        },
      });
  }

  // 按字段 DataType 把文本输入转成对应 JSON 类型——补全的空字段原值为空，
  // 不能只看原值类型。Date/DateTime/String 一律存字符串。
  private coerceValue(name: string, raw: string): unknown {
    const def = this.fieldDefinitions().find(d => d.name === name);
    switch (def?.dataType) {
      case FieldDataType.Integer:
      case FieldDataType.Decimal: {
        const n = Number(raw);
        return raw.trim() !== '' && !Number.isNaN(n) ? n : raw;
      }
      case FieldDataType.Boolean:
        return raw === 'true' || raw === '1';
      default:
        return raw;
    }
  }

  fieldDataTypeLabel(dataType: FieldDataType): string {
    return FieldDataType[dataType] ?? '';
  }

  formatElapsed(run: DocumentPipelineRunDto): string {
    const ms = this.getElapsedMs(run);
    if (ms == null) return '';
    if (ms < 1000) return `${ms} ms`;
    const seconds = ms / 1000;
    if (seconds < 60) return `${seconds.toFixed(1)} s`;
    const minutes = Math.floor(seconds / 60);
    const remSeconds = Math.round(seconds - minutes * 60);
    return `${minutes}m ${remSeconds}s`;
  }

  protected pickLatestRun(runs: DocumentPipelineRunDto[], pipelineCode: string): DocumentPipelineRunDto | null {
    const matches = runs.filter(r => r.pipelineCode === pipelineCode);
    if (matches.length === 0) return null;
    return matches.reduce((prev, curr) => (curr.attemptNumber > prev.attemptNumber ? curr : prev));
  }
}
