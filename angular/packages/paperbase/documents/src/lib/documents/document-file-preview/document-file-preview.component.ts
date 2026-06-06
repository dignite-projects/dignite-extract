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
import { ActivatedRoute, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { LocalizationPipe } from '@abp/ng.core';
import { DocumentDto, DocumentService } from '@dignite/paperbase';
import { DocumentFileBlobService } from '../../shared/document-file-blob.service';
import { isImageContentType, isPdfContentType } from '../../shared/content-type';

// 文件预览页（路由 documents/:id/file）。替代旧详情页 openFile() 的 blob: 新标签直开——
// 地址栏改为可读的 /documents/{id}/file，含文档 ID。文件本体仍经 DocumentService.getBlob
// （带 Bearer token）拉取后用 blob 内嵌，token 不进 URL。blob 生命周期统一交 DocumentFileBlobService（#277）。
@Component({
  selector: 'lib-document-file-preview',
  templateUrl: './document-file-preview.component.html',
  imports: [CommonModule, LocalizationPipe],
  providers: [DocumentFileBlobService],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentFilePreviewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly documentService = inject(DocumentService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly fileBlob = inject(DocumentFileBlobService);

  document = signal<DocumentDto | null>(null);
  isLoading = signal(true);
  hasError = signal(false);

  private documentId!: string;

  readonly fileName = computed(
    () => this.document()?.fileOrigin?.originalFileName || this.document()?.title || '',
  );
  readonly contentType = computed(() => this.document()?.fileOrigin?.contentType ?? '');
  readonly isImage = computed(() => isImageContentType(this.contentType()));
  readonly isPdf = computed(() => isPdfContentType(this.contentType()));

  ngOnInit(): void {
    this.documentId = this.route.snapshot.paramMap.get('id')!;
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.hasError.set(false);
    // 先取元数据拿 contentType / 文件名决定渲染方式，再交 service 拉 blob 本体。
    this.documentService
      .get(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: doc => {
          this.document.set(doc);
          this.isLoading.set(false);
          this.fileBlob.ensureLoaded(this.documentId);
        },
        error: () => {
          this.isLoading.set(false);
          this.hasError.set(true);
        },
      });
  }

  back(): void {
    this.router.navigate(['/documents', this.documentId]);
  }
}
