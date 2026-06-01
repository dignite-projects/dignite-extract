// Mirrors C# Dignite.Paperbase.Documents.FieldDataType (Domain.Shared).
export enum FieldDataType {
  String = 0,
  Number = 1,
  Boolean = 2,
  Date = 3,
  DateTime = 4,
}

// `key` is a localization key (rendered via the `abpLocalization` pipe) rather
// than the raw enum member name, so dropdowns/badges show translated labels.
export const fieldDataTypeOptions = [
  { key: '::FieldDataType:String', value: FieldDataType.String },
  { key: '::FieldDataType:Number', value: FieldDataType.Number },
  { key: '::FieldDataType:Boolean', value: FieldDataType.Boolean },
  { key: '::FieldDataType:Date', value: FieldDataType.Date },
  { key: '::FieldDataType:DateTime', value: FieldDataType.DateTime },
];
