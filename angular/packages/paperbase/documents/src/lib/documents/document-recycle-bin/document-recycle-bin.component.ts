import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import type { PagedResultDto } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import {
  DocumentListItemDto,
  DocumentService,
  PAPERBASE_PERMISSIONS,
} from '@dignite/paperbase';

@Component({
  selector: 'lib-document-recycle-bin',
  templateUrl: './document-recycle-bin.component.html',
  styleUrls: ['./document-recycle-bin.component.scss'],
  imports: [CommonModule, FormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentRecycleBinComponent implements OnInit {
  private readonly documentService = inject(DocumentService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  documents = signal<PagedResultDto<DocumentListItemDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);
  page = signal(0);
  pageSize = 10;
  totalPages = computed(() => Math.ceil((this.documents().totalCount ?? 0) / this.pageSize));
  paginationPages = computed(() => Array.from({ length: this.totalPages() }, (_, i) => i));

  readonly canRestore = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Restore,
  );
  readonly canPermanentDelete = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.PermanentDelete,
  );

  ngOnInit(): void {
    this.loadList();
  }

  refresh(): void {
    this.loadList();
  }

  navigateTo(page: number): void {
    this.page.set(page);
    this.loadList();
  }

  private loadList(): void {
    this.isLoading.set(true);
    this.documentService
      .getList({
        isDeleted: true,
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

  restore(doc: DocumentListItemDto): void {
    this.confirmation
      .warn('::Document:AreYouSureToRestore', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.documentService.restore(doc.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::Document:RestoredSuccessfully', '::Success');
              this.loadList();
            },
            error: () => this.toaster.error('::Document:RestoreFailed', '::Error'),
          });
      });
  }

  permanentDelete(doc: DocumentListItemDto): void {
    this.confirmation
      .warn('::Document:AreYouSureToPermanentlyDelete', '::AreYouSure', {
        yesText: '::Document:PermanentDelete',
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.documentService.permanentDelete(doc.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::Document:PermanentlyDeletedSuccessfully', '::Success');
              this.loadList();
            },
            error: () => this.toaster.error('::Document:PermanentDeleteFailed', '::Error'),
          });
      });
  }

  isImage(doc: DocumentListItemDto): boolean {
    return doc.fileOrigin?.contentType?.startsWith('image/') ?? false;
  }

  formatFileSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
}
