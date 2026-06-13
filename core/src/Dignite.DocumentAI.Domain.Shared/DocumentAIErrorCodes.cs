namespace Dignite.DocumentAI;

/// <summary>
/// Error-code strings: wire-level protocol contract used as i18n dictionary keys and by downstream
/// consumers branching on code. They must be <c>const</c>: any runtime change would break mappings in
/// Localization/DocumentAI/*.json and downstream try/catch logic such as
/// (code == "DocumentAI:Xxx"). Nested static classes are only aggregate-based grouping drawers; C#
/// identifier paths can be adjusted, but <b>string values are frozen contracts</b> and correspond
/// one-to-one with Localization/DocumentAI/*.json keys.
/// </summary>
public static class DocumentAIErrorCodes
{
    public static class Document
    {
        public const string MarkdownIsImmutable = "DocumentAI:MarkdownIsImmutable";
        public const string TitleIsImmutable = "DocumentAI:TitleIsImmutable";
        public const string Duplicate = "DocumentAI:DocumentDuplicate";
        public const string InRecycleBin = "DocumentAI:DocumentInRecycleBin";
        public const string NotClassified = "DocumentAI:DocumentNotClassified";
        // #263: prerequisite for "re-recognize" (rerun automatic classification). Automatic
        // classification input is Document.Markdown, so without text extraction output there is
        // nothing to reclassify.
        public const string NotTextExtracted = "DocumentAI:DocumentNotTextExtracted";
        // #221: upload fail-closed validation failure codes (size exceeded / content-type + extension
        // not in whitelist).
        public const string FileTooLarge = "DocumentAI:DocumentFileTooLarge";
        public const string UnsupportedFileType = "DocumentAI:DocumentUnsupportedFileType";
    }

    public static class DocumentType
    {
        public const string InvalidCodeFormat = "DocumentAI:InvalidDocumentTypeCodeFormat";
        public const string CodeAlreadyExists = "DocumentAI:DocumentTypeCodeAlreadyExists";
        public const string InUse = "DocumentAI:DocumentTypeInUse";
        public const string RestoreConflict = "DocumentAI:DocumentTypeRestoreConflict";
        public const string InvalidDisplayName = "DocumentAI:InvalidDocumentTypeDisplayName";
        public const string InvalidDescription = "DocumentAI:InvalidDocumentTypeDescription";
        public const string NoneConfigured = "DocumentAI:NoDocumentTypesConfigured";
    }

    public static class FieldDefinition
    {
        public const string AlreadyExists = "DocumentAI:FieldDefinitionAlreadyExists";
        public const string InvalidName = "DocumentAI:InvalidFieldDefinitionName";
        public const string InvalidDisplayName = "DocumentAI:InvalidFieldDefinitionDisplayName";
        public const string RestoreConflict = "DocumentAI:FieldDefinitionRestoreConflict";
        public const string ParentTypeMissing = "DocumentAI:FieldDefinitionParentTypeMissing";
        public const string DataTypeChangeNotAllowed = "DocumentAI:FieldDefinitionDataTypeChangeNotAllowed";
        public const string MultiValueRequiresStringType = "DocumentAI:FieldDefinitionMultiValueRequiresStringType";
        public const string MultiValueChangeNotAllowed = "DocumentAI:FieldDefinitionMultiValueChangeNotAllowed";
    }

    public static class ExtractedField
    {
        public const string Unknown = "DocumentAI:UnknownExtractedField";
        public const string InvalidValue = "DocumentAI:InvalidExtractedFieldValue";
        public const string FieldTypeDoesNotSupportRange = "DocumentAI:FieldTypeDoesNotSupportRange";
        public const string FieldTypeNotQueryable = "DocumentAI:FieldTypeNotQueryable";
    }

    public static class Pipeline
    {
        public const string NotRetryable = "DocumentAI:PipelineNotRetryable";
        public const string RetryInProgress = "DocumentAI:PipelineRetryInProgress";
        public const string NeverRan = "DocumentAI:PipelineNeverRan";
        public const string UnknownCode = "DocumentAI:UnknownPipelineCode";
    }

    public static class Export
    {
        public const string InvalidTemplateName = "DocumentAI:InvalidExportTemplateName";
        public const string TemplateNameAlreadyExists = "DocumentAI:ExportTemplateNameAlreadyExists";
        public const string TemplateRequiresColumn = "DocumentAI:ExportTemplateRequiresColumn";
        public const string TemplateTooManyColumns = "DocumentAI:ExportTemplateTooManyColumns";
        public const string TemplateDuplicateField = "DocumentAI:ExportTemplateDuplicateField";
        public const string DocumentLimitExceeded = "DocumentAI:ExportDocumentLimitExceeded";
    }

    // Cabinets (#194).
    public static class Cabinet
    {
        public const string InvalidName = "DocumentAI:InvalidCabinetName";
        public const string InvalidDescription = "DocumentAI:InvalidCabinetDescription";
        public const string NameAlreadyExists = "DocumentAI:CabinetNameAlreadyExists";
        public const string InvalidId = "DocumentAI:InvalidCabinetId";
    }
}
