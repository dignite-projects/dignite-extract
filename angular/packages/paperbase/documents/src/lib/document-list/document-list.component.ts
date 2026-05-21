import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
  computed,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import type { PagedResultDto } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { Confirmation } from '@abp/ng.theme.shared';
import {
  DocumentDto,
  DocumentLifecycleStatus,
  DocumentPipelineRunDto,
  DocumentReviewStatus,
  DocumentService,
  GetDocumentListInput,
  PAPERBASE_PERMISSIONS,
  PipelineRunCandidate,
} from '@dignite/paperbase';
import { from, of } from 'rxjs';
import { catchError, map, mergeMap } from 'rxjs/operators';

// Mirrors document-upload.component.ts. Limits concurrent /upload requests
// so a 50-file drop does not saturate the browser connection pool.
const MAX_CONCURRENT_UPLOADS = 3;

interface UploadResult {
  fileName: string;
  documentId?: string;
  succeeded: boolean;
  errorMessage?: string;
}

@Component({
  selector: 'lib-document-list',
  templateUrl: './document-list.component.html',
  styleUrls: ['./document-list.component.scss'],
  imports: [CommonModule, RouterModule, FormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentListComponent implements OnInit {
  private readonly documentService = inject(DocumentService);
  private readonly router = inject(Router);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canDelete = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Delete,
  );

  documents = signal<PagedResultDto<DocumentDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  isExporting = signal(false);
  isBulkUploading = signal(false);
  bulkUploadResults = signal<UploadResult[]>([]);

  reviewStatusFilter = signal<DocumentReviewStatus | undefined>(undefined);
  confirmingDoc = signal<DocumentDto | null>(null);
  selectedTypeCode = signal('');
  isConfirming = signal(false);

  page = signal(0);
  pageSize = 10;
  totalPages = computed(() => Math.ceil(this.documents().totalCount / this.pageSize));
  paginationPages = computed(() => Array.from({ length: this.totalPages() }, (_, i) => i));
  pendingReviewCount = computed(() =>
    this.documents().items.filter(d => d.reviewStatus === DocumentReviewStatus.PendingReview).length
  );

  private activeFilter: GetDocumentListInput = {};

  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;
  readonly DocumentReviewStatus = DocumentReviewStatus;

  ngOnInit(): void {
    this.loadList();
  }

  refresh(): void {
    this.loadList();
  }

  private loadList(): void {
    this.isLoading.set(true);
    this.documentService.getList({
      ...this.activeFilter,
      maxResultCount: this.pageSize,
      skipCount: this.page() * this.pageSize,
      sorting: 'creationTime desc',
      reviewStatus: this.reviewStatusFilter(),
    })
    .pipe(takeUntilDestroyed(this.destroyRef))
    .subscribe({
      next: result => {
        this.documents.set(result);
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
      },
    });
  }

  navigateTo(page: number): void {
    this.page.set(page);
    this.loadList();
  }

  openDetail(doc: DocumentDto): void {
    this.router.navigate(['/documents', doc.id]);
  }

  uploadNew(): void {
    this.router.navigate(['/documents/upload']);
  }

  onBulkFileChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files?.length) return;
    const files = Array.from(input.files);

    this.isBulkUploading.set(true);
    this.bulkUploadResults.set([]);

    from(files)
      .pipe(
        mergeMap(
          file =>
            this.documentService.upload(file).pipe(
              map(doc => ({ fileName: file.name, documentId: doc.id, succeeded: true } as UploadResult)),
              catchError(err =>
                of({ fileName: file.name, succeeded: false, errorMessage: err?.message } as UploadResult),
              ),
            ),
          MAX_CONCURRENT_UPLOADS,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: result => this.bulkUploadResults.update(r => [...r, result]),
        complete: () => {
          this.isBulkUploading.set(false);
          this.loadList();
          input.value = '';
        },
      });
  }

  exportCsv(): void {
    const url = this.documentService.getExportUrl(this.activeFilter);
    window.open(url, '_blank');
  }

  delete(doc: DocumentDto, event: Event): void {
    event.stopPropagation();
    this.confirmation
      .warn('::Document:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status === Confirmation.Status.confirm) {
          this.documentService.delete(doc.id)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe({
            next: () => {
              this.toaster.success('::Document:DeletedSuccessfully', '::Success');
              this.loadList();
            },
          });
        }
      });
  }

  toggleManualReviewFilter(): void {
    this.reviewStatusFilter.update(v =>
      v === DocumentReviewStatus.PendingReview ? undefined : DocumentReviewStatus.PendingReview
    );
    this.page.set(0);
    this.loadList();
  }

  getLatestClassificationRun(doc: DocumentDto): DocumentPipelineRunDto | null {
    const runs = doc.pipelineRuns?.filter(r => r.pipelineCode === 'classification') ?? [];
    if (runs.length === 0) return null;
    return runs.reduce((prev, curr) => curr.attemptNumber > prev.attemptNumber ? curr : prev);
  }

  needsConfirmation(doc: DocumentDto): boolean {
    return doc.reviewStatus === DocumentReviewStatus.PendingReview;
  }

  isProcessingDocument(doc: DocumentDto): boolean {
    return doc.reviewStatus !== DocumentReviewStatus.PendingReview &&
      (doc.lifecycleStatus === DocumentLifecycleStatus.Processing ||
       doc.lifecycleStatus === DocumentLifecycleStatus.Uploaded);
  }

  getCandidates(doc: DocumentDto): PipelineRunCandidate[] {
    const run = this.getLatestClassificationRun(doc);
    const fromRun = run?.candidates;
    if (fromRun && fromRun.length > 0) return fromRun;
    return doc.documentTypeCode ? [{ typeCode: doc.documentTypeCode, confidenceScore: 1 }] : [];
  }

  openConfirmDialog(doc: DocumentDto, event: Event): void {
    event.stopPropagation();
    this.confirmingDoc.set(doc);
    const candidates = this.getCandidates(doc);
    const defaultCode = candidates[0]?.typeCode ?? doc.documentTypeCode ?? '';
    this.selectedTypeCode.set(defaultCode);
  }

  closeConfirmDialog(): void {
    this.confirmingDoc.set(null);
    this.selectedTypeCode.set('');
  }

  onTypeCodeInput(event: Event): void {
    this.selectedTypeCode.set((event.target as HTMLInputElement).value);
  }

  submitConfirmation(): void {
    const doc = this.confirmingDoc();
    if (!doc || !this.selectedTypeCode()) return;
    this.isConfirming.set(true);
    this.documentService.confirmClassification(doc.id, this.selectedTypeCode())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
      next: () => {
        this.isConfirming.set(false);
        this.closeConfirmDialog();
        this.toaster.success('::Document:ClassificationConfirmed', '::Success');
        this.loadList();
      },
      error: () => {
        this.isConfirming.set(false);
        this.toaster.error('::Document:ConfirmFailed', '::Error');
      },
    });
  }

  getStatusBadgeClass(status: DocumentLifecycleStatus): string {
    switch (status) {
      case DocumentLifecycleStatus.Uploaded:
        return 'badge bg-secondary';
      case DocumentLifecycleStatus.Processing:
        return 'badge bg-warning text-dark';
      case DocumentLifecycleStatus.Ready:
        return 'badge bg-success';
      case DocumentLifecycleStatus.Failed:
        return 'badge bg-danger';
      default:
        return 'badge bg-secondary';
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
      case DocumentLifecycleStatus.Uploaded:
        return '::Document:Status:Uploaded';
      case DocumentLifecycleStatus.Processing:
        return '::Document:Status:Processing';
      case DocumentLifecycleStatus.Ready:
        return '::Document:Status:Ready';
      case DocumentLifecycleStatus.Failed:
        return '::Document:Status:Failed';
      default:
        return '::Document:Status:Unknown';
    }
  }

  getDocumentStatusLabel(doc: DocumentDto): string {
    if (doc.reviewStatus === DocumentReviewStatus.PendingReview) {
      return '::DocumentReviewStatus:PendingReview';
    }

    return this.getStatusLabel(doc.lifecycleStatus);
  }

  isImage(doc: DocumentDto): boolean {
    return doc.fileOrigin?.contentType?.startsWith('image/') ?? false;
  }

  formatFileSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
}
