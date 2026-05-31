import type { EntityDto } from '@abp/ng.core';
import type { FieldDataType } from './field-data-type.enum';

// Mirrors C# Dignite.Paperbase.Documents.Fields.FieldDefinitionDto.
// 类型绑定字段（B 机制）。按 (tenantId, documentTypeId, name) 唯一，两层互斥不混合。
// documentTypeId 为不可变 Id（#207：TypeCode 可重命名，故不作引用键）。
export interface FieldDefinitionDto extends EntityDto<string> {
  tenantId?: string;
  documentTypeId: string;
  name: string;
  displayName: string;
  prompt: string;
  dataType: FieldDataType;
  displayOrder: number;
  isRequired: boolean;
  // #212：是否允许多值。仅 dataType === String 时有效（后端实体层 loud fail 非 String + 多值）。
  allowMultiple: boolean;
}

export interface CreateFieldDefinitionDto {
  documentTypeId: string;
  name: string;
  displayName: string;
  prompt: string;
  dataType: FieldDataType;
  displayOrder: number;
  isRequired: boolean;
  allowMultiple: boolean;
}

export interface UpdateFieldDefinitionDto {
  name: string;
  displayName: string;
  prompt: string;
  dataType: FieldDataType;
  displayOrder: number;
  isRequired: boolean;
  allowMultiple: boolean;
}

// Mirrors C# Dignite.Paperbase.Documents.Fields.GetFieldDefinitionListInput.
// onlyDeleted=false（默认）取活跃字段（按 displayOrder）；true 取回收站（已软删除，按 deletionTime 倒序）。两视图互斥。
export interface GetFieldDefinitionListInput {
  documentTypeId: string;
  onlyDeleted?: boolean;
}
