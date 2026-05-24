import { mapEnumToOptions } from '@abp/ng.core';

// Mirrors C# Dignite.Paperbase.Documents.ExportFormat (Domain.Shared).
export enum ExportFormat {
  Csv = 0,
  Xlsx = 1,
}

export const exportFormatOptions = mapEnumToOptions(ExportFormat);
