import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe } from '@abp/ng.core';
import type { PagedResultDto } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import {
  DocumentListItemDto,
  DocumentReviewStatus,
  DocumentService,
  DocumentTypeDto,
  DocumentTypeService,
} from '@dignite/paperbase';

@Component({
  selector: 'lib-document-review-queue',
  templateUrl: './document-review-queue.component.html',
  styleUrls: ['./document-review-queue.component.scss'],
  imports: [CommonModule, FormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentReviewQueueComponent implements OnInit {
  private readonly documentService = inject(DocumentService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly router = inject(Router);
  private readonly toaster = inject(ToasterService);
  private readonly destroyRef = inject(DestroyRef);

  documents = signal<PagedResultDto<DocumentListItemDto>>({ totalCount: 0, items: [] });
  documentTypes = signal<DocumentTypeDto[]>([]);
  isLoading = signal(true);
  isSubmitting = signal(false);

  page = signal(0);
  pageSize = 10;
  totalPages = computed(() => Math.ceil(this.documents().totalCount / this.pageSize));
  paginationPages = computed(() => Array.from({ length: this.totalPages() }, (_, i) => i));

  // Confirm/assign-classification dialog state.
  classifyingDoc = signal<DocumentListItemDto | null>(null);
  selectedTypeId = signal('');

  // Reject dialog state.
  rejectingDoc = signal<DocumentListItemDto | null>(null);
  rejectReason = signal('');

  ngOnInit(): void {
    this.loadList();
    this.loadDocumentTypes();
  }

  refresh(): void {
    this.loadList();
  }

  private loadList(): void {
    this.isLoading.set(true);
    this.documentService
      .getList({
        reviewStatus: DocumentReviewStatus.PendingReview,
        maxResultCount: this.pageSize,
        skipCount: this.page() * this.pageSize,
        sorting: 'creationTime desc',
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.documents.set(result);
          this.isLoading.set(false);
        },
        error: () => this.isLoading.set(false),
      });
  }

  private loadDocumentTypes(): void {
    this.documentTypeService
      .getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: types => this.documentTypes.set(types),
        error: () => this.documentTypes.set([]),
      });
  }

  navigateTo(page: number): void {
    this.page.set(page);
    this.loadList();
  }

  openDetail(doc: DocumentListItemDto): void {
    this.router.navigate(['/documents', doc.id]);
  }

  openClassifyDialog(doc: DocumentListItemDto, event: Event): void {
    event.stopPropagation();
    this.classifyingDoc.set(doc);
    // The confirm command is keyed by immutable DocumentTypeId (#207); resolve the
    // document's exit-contract typeCode → id via the already-loaded visible types.
    this.selectedTypeId.set(
      this.documentTypes().find(t => t.typeCode === doc.documentTypeCode)?.id ?? '',
    );
  }

  closeClassifyDialog(): void {
    this.classifyingDoc.set(null);
    this.selectedTypeId.set('');
  }

  submitClassify(): void {
    const doc = this.classifyingDoc();
    if (!doc || !this.selectedTypeId()) return;
    this.isSubmitting.set(true);
    this.documentService
      .confirmClassification(doc.id, this.selectedTypeId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSubmitting.set(false);
          this.closeClassifyDialog();
          this.toaster.success('::Document:ClassificationConfirmed', '::Success');
          this.loadList();
        },
        error: () => {
          this.isSubmitting.set(false);
          this.toaster.error('::Document:ConfirmFailed', '::Error');
        },
      });
  }

  openRejectDialog(doc: DocumentListItemDto, event: Event): void {
    event.stopPropagation();
    this.rejectingDoc.set(doc);
    this.rejectReason.set('');
  }

  closeRejectDialog(): void {
    this.rejectingDoc.set(null);
    this.rejectReason.set('');
  }

  submitReject(): void {
    const doc = this.rejectingDoc();
    if (!doc) return;
    this.isSubmitting.set(true);
    const reason = this.rejectReason().trim();
    this.documentService
      .rejectReview(doc.id, reason || undefined)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSubmitting.set(false);
          this.closeRejectDialog();
          this.toaster.success('::Document:Review:RejectedSuccessfully', '::Success');
          this.loadList();
        },
        error: () => {
          this.isSubmitting.set(false);
          this.toaster.error('::Document:Review:ActionFailed', '::Error');
        },
      });
  }

  confidencePercent(doc: DocumentListItemDto): number {
    return Math.round((doc.classificationConfidence ?? 0) * 100);
  }

  isImage(doc: DocumentListItemDto): boolean {
    return doc.fileOrigin?.contentType?.startsWith('image/') ?? false;
  }
}
