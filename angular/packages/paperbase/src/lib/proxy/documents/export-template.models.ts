import type { EntityDto } from '@abp/ng.core';
import type { DocumentLifecycleStatus } from './document-lifecycle-status.enum';
import type { ExportColumnSourceKind } from './export-column-source-kind.enum';
import type { ExportFormat } from './export-format.enum';

// Mirrors C# Dignite.Paperbase.Documents.ExportColumnDto / ExportTemplateDto / inputs.
export interface ExportColumnDto {
  sourceKind: ExportColumnSourceKind;
  key: string;
  columnName: string;
  order: number;
}

export interface ExportTemplateDto extends EntityDto<string> {
  tenantId?: string;
  name: string;
  format: ExportFormat;
  documentTypeCode?: string;
  columns: ExportColumnDto[];
}

export interface ExportColumnInput {
  sourceKind: ExportColumnSourceKind;
  key: string;
  columnName: string;
  order: number;
}

export interface CreateExportTemplateDto {
  name: string;
  format: ExportFormat;
  documentTypeCode?: string;
  columns: ExportColumnInput[];
}

export interface UpdateExportTemplateDto {
  name: string;
  format: ExportFormat;
  documentTypeCode?: string;
  columns: ExportColumnInput[];
}

// DocumentIds non-empty wins; otherwise the filter applies. Subject to the per-export document cap.
export interface ExportDocumentsInput {
  templateId: string;
  documentIds?: string[];
  lifecycleStatus?: DocumentLifecycleStatus;
  documentTypeCode?: string;
  keyword?: string;
}
