// Mirrors C# Dignite.Paperbase.Documents.ExportFormat (Domain.Shared).
export enum ExportFormat {
  Csv = 0,
  Xlsx = 1,
}

// `key` is a localization key (rendered via the `abpLocalization` pipe) rather
// than the raw enum member name, so dropdowns/badges show translated labels.
export const exportFormatOptions = [
  { key: '::ExportFormat:Csv', value: ExportFormat.Csv },
  { key: '::ExportFormat:Xlsx', value: ExportFormat.Xlsx },
];
