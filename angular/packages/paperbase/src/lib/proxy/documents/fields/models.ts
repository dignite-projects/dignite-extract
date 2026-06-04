import type { FieldDataType } from './field-data-type.enum';
import type { EntityDto } from '@abp/ng.core';

export interface CreateFieldDefinitionDto {
  documentTypeId: string;
  name: string;
  displayName: string;
  prompt?: string | null;
  dataType?: FieldDataType;
  displayOrder?: number;
  isRequired?: boolean;
  allowMultiple?: boolean;
}

export interface FieldDefinitionDto extends EntityDto<string> {
  tenantId?: string | null;
  documentTypeId?: string;
  name?: string;
  displayName?: string;
  prompt?: string | null;
  dataType?: FieldDataType;
  displayOrder?: number;
  isRequired?: boolean;
  allowMultiple?: boolean;
}

export interface GetFieldDefinitionListInput {
  documentTypeId: string;
  onlyDeleted?: boolean;
}

export interface UpdateFieldDefinitionDto {
  name: string;
  displayName: string;
  prompt?: string | null;
  dataType?: FieldDataType;
  displayOrder?: number;
  isRequired?: boolean;
  allowMultiple?: boolean;
}
