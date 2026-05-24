export const PAPERBASE_PERMISSIONS = {
  Documents: {
    Default: 'Paperbase.Documents',
    Upload: 'Paperbase.Documents.Upload',
    Delete: 'Paperbase.Documents.Delete',
    PermanentDelete: 'Paperbase.Documents.PermanentDelete',
    Restore: 'Paperbase.Documents.Restore',
    Export: 'Paperbase.Documents.Export',
    ConfirmClassification: 'Paperbase.Documents.ConfirmClassification',
    Pipelines: {
      Default: 'Paperbase.Documents.Pipelines',
      Retry: 'Paperbase.Documents.Pipelines.Retry',
    },
    Templates: {
      Default: 'Paperbase.Documents.Templates',
      Create: 'Paperbase.Documents.Templates.Create',
      Update: 'Paperbase.Documents.Templates.Update',
      Delete: 'Paperbase.Documents.Templates.Delete',
    },
  },
  Cabinets: {
    Default: 'Paperbase.Cabinets',
    Create: 'Paperbase.Cabinets.Create',
    Update: 'Paperbase.Cabinets.Update',
    Delete: 'Paperbase.Cabinets.Delete',
  },
} as const;
