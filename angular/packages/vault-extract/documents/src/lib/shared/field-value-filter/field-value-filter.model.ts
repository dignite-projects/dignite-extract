import { DocumentFieldFilter, FieldDataType } from '@dignite/vault-extract';

export type FilterMode = 'eq' | 'range';

// One in-progress editor row for the field-value composer. Deliberately richer than the server contract
// (DocumentFieldFilter): it carries the resolved dataType + mode so the UI can render the right input, and
// is compiled down to DocumentFieldFilter only on Apply, dropping incomplete rows.
export interface FilterRow {
  key: number;
  fieldName: string;
  dataType: FieldDataType;
  mode: FilterMode;
  value: string;
  min: string;
  max: string;
}

// Only Number / Date / DateTime support ranges. Text / Boolean are equality-only (the server hard-errors a
// range on them), and LongText is not queryable at all.
export function rangeSupported(dataType: FieldDataType): boolean {
  return (
    dataType === FieldDataType.Number ||
    dataType === FieldDataType.Date ||
    dataType === FieldDataType.DateTime
  );
}

/**
 * Compile editor rows into server-shaped {@link DocumentFieldFilter} values. Incomplete rows — no field
 * chosen, or no value / no bound entered — are dropped so the request never trips the server's "at least
 * one of value/min/max" guard (which would otherwise be an AbpValidationException). A range is emitted only
 * for range-capable types; Text/Boolean always compile to equality, so a range (rejected server-side for
 * them) can never be built. Values are trimmed and emitted as strings exactly as the server parsers expect.
 */
export function composeFieldFilters(rows: readonly FilterRow[]): DocumentFieldFilter[] {
  const filters: DocumentFieldFilter[] = [];
  for (const r of rows) {
    if (!r.fieldName) {
      continue;
    }
    if (r.mode === 'range' && rangeSupported(r.dataType)) {
      const min = r.min.trim();
      const max = r.max.trim();
      if (min || max) {
        filters.push({ name: r.fieldName, min: min || null, max: max || null });
      }
    } else {
      const value = r.value.trim();
      if (value) {
        filters.push({ name: r.fieldName, value });
      }
    }
  }
  return filters;
}
