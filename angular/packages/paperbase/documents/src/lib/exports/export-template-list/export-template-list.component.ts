import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormArray, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import {
  CreateExportTemplateDto,
  DocumentTypeDto,
  DocumentTypeService,
  ExportColumnInput,
  ExportFormat,
  ExportTemplateDto,
  ExportTemplateService,
  FieldDefinitionDto,
  FieldDefinitionService,
  PAPERBASE_PERMISSIONS,
  exportFormatOptions,
} from '@dignite/paperbase';

// Mirrors ExportTemplateConsts (Domain.Shared).
const MAX_NAME_LENGTH = 128;
const MAX_COLUMN_NAME_LENGTH = 128;

@Component({
  selector: 'lib-export-template-list',
  templateUrl: './export-template-list.component.html',
  styleUrls: ['./export-template-list.component.scss'],
  imports: [CommonModule, ReactiveFormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExportTemplateListComponent implements OnInit {
  private readonly service = inject(ExportTemplateService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly fieldDefinitionService = inject(FieldDefinitionService);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canManage = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Templates.Default,
  );
  readonly canExport = this.permissionService.getGrantedPolicy(
    PAPERBASE_PERMISSIONS.Documents.Export,
  );

  readonly formatOptions = exportFormatOptions;
  readonly ExportFormat = ExportFormat;

  templates = signal<ExportTemplateDto[]>([]);
  documentTypes = signal<DocumentTypeDto[]>([]);
  fieldDefinitions = signal<FieldDefinitionDto[]>([]);
  isLoading = signal(true);
  editing = signal<ExportTemplateDto | 'create' | null>(null);
  isSubmitting = signal(false);
  exportingId = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(MAX_NAME_LENGTH)]],
    format: [ExportFormat.Csv, [Validators.required]],
    documentTypeId: ['', [Validators.required]],
    columns: this.fb.array<FormGroup>([]),
  });

  get columns(): FormArray<FormGroup> {
    return this.form.controls.columns;
  }

  ngOnInit(): void {
    this.load();
    this.loadDocumentTypes();
  }

  refresh(): void {
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.service
      .getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => {
          this.templates.set(list);
          this.isLoading.set(false);
        },
        error: () => this.isLoading.set(false),
      });
  }

  private loadDocumentTypes(): void {
    this.documentTypeService
      .getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: list => this.documentTypes.set(list) });
  }

  openCreate(): void {
    this.columns.clear();
    this.fieldDefinitions.set([]);
    this.form.reset({ name: '', format: ExportFormat.Csv, documentTypeId: '' });
    this.addColumn();
    this.editing.set('create');
  }

  openEdit(template: ExportTemplateDto): void {
    this.columns.clear();
    this.form.reset({
      name: template.name,
      format: template.format,
      documentTypeId: template.documentTypeId,
    });
    this.loadFieldDefinitions(template.documentTypeId);
    [...template.columns]
      .sort((a, b) => a.order - b.order)
      .forEach(c => this.addColumn(c.fieldDefinitionId, c.columnName));
    this.editing.set(template);
  }

  addColumn(fieldDefinitionId = '', columnName = ''): void {
    this.columns.push(
      this.fb.nonNullable.group({
        fieldDefinitionId: [fieldDefinitionId, [Validators.required]],
        columnName: [columnName, [Validators.required, Validators.maxLength(MAX_COLUMN_NAME_LENGTH)]],
      }),
    );
  }

  removeColumn(index: number): void {
    this.columns.removeAt(index);
  }

  // Switching the document type invalidates already-picked field columns (they belong to the prior type).
  onDocumentTypeChange(): void {
    const documentTypeId = this.form.controls.documentTypeId.value;
    this.columns.controls.forEach(ctrl => ctrl.get('fieldDefinitionId')?.setValue(''));
    this.loadFieldDefinitions(documentTypeId);
  }

  private loadFieldDefinitions(documentTypeId: string): void {
    if (!documentTypeId) {
      this.fieldDefinitions.set([]);
      return;
    }
    this.fieldDefinitionService
      .getList({ documentTypeId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: defs => this.fieldDefinitions.set(defs),
        error: () => this.fieldDefinitions.set([]),
      });
  }

  closeModal(): void {
    this.editing.set(null);
  }

  submit(): void {
    if (this.form.invalid || this.columns.length === 0) {
      this.form.markAllAsTouched();
      return;
    }
    const mode = this.editing();
    if (mode === null) return;

    this.isSubmitting.set(true);
    const raw = this.form.getRawValue();
    // Order = array position; the editor's row order is the source of truth.
    const columns: ExportColumnInput[] = this.columns.controls.map((ctrl, i) => {
      const v = ctrl.getRawValue() as {
        fieldDefinitionId: string;
        columnName: string;
      };
      return { fieldDefinitionId: v.fieldDefinitionId, columnName: v.columnName, order: i };
    });

    if (mode === 'create') {
      const input: CreateExportTemplateDto = {
        name: raw.name,
        format: raw.format,
        documentTypeId: raw.documentTypeId,
        columns,
      };
      this.service
        .create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::ExportTemplate:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service
        .update(mode.id!, {
          name: raw.name,
          format: raw.format,
          documentTypeId: raw.documentTypeId,
          columns,
        })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::ExportTemplate:UpdatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    }
  }

  private onSaved(messageKey: string): void {
    this.isSubmitting.set(false);
    this.closeModal();
    this.toaster.success(messageKey, '::Success');
    this.load();
  }

  delete(template: ExportTemplateDto): void {
    this.confirmation
      .warn('::ExportTemplate:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.service
          .delete(template.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::ExportTemplate:DeletedSuccessfully', '::Success');
              this.load();
            },
            error: () => this.toaster.error('::ExportTemplate:DeleteFailed', '::Error'),
          });
      });
  }

  exportTemplate(template: ExportTemplateDto): void {
    this.exportingId.set(template.id!);
    // v1: export all documents the template applies to (backend enforces the per-export cap).
    // Document-checkbox / filter selection can be layered on later from the document list.
    this.service
      .export({ templateId: template.id! })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => {
          const ext = template.format === ExportFormat.Xlsx ? '.xlsx' : '.csv';
          this.triggerDownload(blob, template.name + ext);
          this.exportingId.set(null);
        },
        error: () => this.exportingId.set(null),
      });
  }

  private triggerDownload(blob: Blob, fileName: string): void {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    // Defer revoke so the browser's download handler has taken ownership of the
    // blob before its backing URL is released (avoids a race on large files).
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }

  formatLabel(format: ExportFormat): string {
    return this.formatOptions.find(o => o.value === format)?.key ?? String(format);
  }

  documentTypeLabel(documentTypeId: string): string | null {
    return this.documentTypes().find(dt => dt.id === documentTypeId)?.typeCode ?? null;
  }
}
