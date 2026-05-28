import { Injectable, inject } from '@angular/core';
import { RestService } from '@abp/ng.core';
import { Observable } from 'rxjs';
import type {
  CreateFieldDefinitionDto,
  FieldDefinitionDto,
  UpdateFieldDefinitionDto,
} from '../../documents/field-definition.models';

// Backend: Dignite.Paperbase.HttpApi.Documents.Fields.FieldDefinitionController (/api/paperbase/field-definitions).
@Injectable({ providedIn: 'root' })
export class FieldDefinitionService {
  private readonly rest = inject(RestService);
  private readonly apiName = 'Default';
  private readonly basePath = '/api/paperbase/field-definitions';

  // 当前层指定文档类型下的字段定义（不跨层）。
  getByDocumentType = (documentTypeCode: string): Observable<FieldDefinitionDto[]> =>
    this.rest.request<void, FieldDefinitionDto[]>(
      { method: 'GET', url: this.basePath, params: { documentTypeCode } },
      { apiName: this.apiName },
    );

  // 当前层指定文档类型下已软删除的字段定义（回收站）。
  getDeletedByDocumentType = (documentTypeCode: string): Observable<FieldDefinitionDto[]> =>
    this.rest.request<void, FieldDefinitionDto[]>(
      { method: 'GET', url: `${this.basePath}/deleted`, params: { documentTypeCode } },
      { apiName: this.apiName },
    );

  create = (input: CreateFieldDefinitionDto): Observable<FieldDefinitionDto> =>
    this.rest.request<CreateFieldDefinitionDto, FieldDefinitionDto>(
      { method: 'POST', url: this.basePath, body: input },
      { apiName: this.apiName },
    );

  update = (id: string, input: UpdateFieldDefinitionDto): Observable<FieldDefinitionDto> =>
    this.rest.request<UpdateFieldDefinitionDto, FieldDefinitionDto>(
      { method: 'PUT', url: `${this.basePath}/${id}`, body: input },
      { apiName: this.apiName },
    );

  delete = (id: string): Observable<void> =>
    this.rest.request<void, void>(
      { method: 'DELETE', url: `${this.basePath}/${id}` },
      { apiName: this.apiName },
    );

  restore = (id: string): Observable<FieldDefinitionDto> =>
    this.rest.request<void, FieldDefinitionDto>(
      { method: 'POST', url: `${this.basePath}/${id}/restore` },
      { apiName: this.apiName },
    );
}
