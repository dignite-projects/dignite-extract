// Mirrors C# Dignite.Paperbase.Documents.FieldDataType (Domain.Shared).
export enum FieldDataType {
  String = 0,
  Number = 1,
  Boolean = 2,
  Date = 3,
  DateTime = 4,
  // 长文本：长内容载荷（摘要 / 描述等），落 nvarchar(max) 列，不索引、不可作查询条件、不支持多值。
  LongText = 5,
}

// `key` is a localization key (rendered via the `abpLocalization` pipe) rather
// than the raw enum member name, so dropdowns/badges show translated labels.
// LongText 排在 String 之后（"短文本/长文本"语义相邻，方便选型），与 enum 数值序无关。
export const fieldDataTypeOptions = [
  { key: '::FieldDataType:String', value: FieldDataType.String },
  { key: '::FieldDataType:LongText', value: FieldDataType.LongText },
  { key: '::FieldDataType:Number', value: FieldDataType.Number },
  { key: '::FieldDataType:Boolean', value: FieldDataType.Boolean },
  { key: '::FieldDataType:Date', value: FieldDataType.Date },
  { key: '::FieldDataType:DateTime', value: FieldDataType.DateTime },
];
