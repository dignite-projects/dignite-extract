import type { EntityDto } from '@abp/ng.core';

export interface CabinetDto extends EntityDto<string> {
  tenantId?: string | null;
  name?: string;
}

export interface CreateCabinetDto {
  name: string;
}

export interface UpdateCabinetDto {
  name: string;
}
