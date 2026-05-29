import type { EntityDto } from '@abp/ng.core';

// Mirrors C# Dignite.Paperbase.Documents.DocumentTypes.DocumentTypeDto.
// 文档类型（字段架构 v2）。GetVisible 返回当前层；CUD / Restore 只作用于当前层。
export interface DocumentTypeDto extends EntityDto<string> {
  tenantId?: string;
  typeCode: string;
  displayName: string;
  confidenceThreshold: number;
  priority: number;
}

export interface CreateDocumentTypeDto {
  typeCode: string;
  displayName: string;
  confidenceThreshold: number;
  priority: number;
}

export interface UpdateDocumentTypeDto {
  typeCode: string;
  displayName: string;
  confidenceThreshold: number;
  priority: number;
}
