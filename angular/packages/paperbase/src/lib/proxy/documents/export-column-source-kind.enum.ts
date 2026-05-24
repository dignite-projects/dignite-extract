import { mapEnumToOptions } from '@abp/ng.core';

// Mirrors C# Dignite.Paperbase.Documents.ExportColumnSourceKind (Domain.Shared).
export enum ExportColumnSourceKind {
  System = 0,
  Extracted = 1,
}

export const exportColumnSourceKindOptions = mapEnumToOptions(ExportColumnSourceKind);
