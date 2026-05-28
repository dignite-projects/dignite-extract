import { Injectable, inject } from '@angular/core';
import { RestService } from '@abp/ng.core';
import { Observable } from 'rxjs';
import type {
  CreateDocumentTypeDto,
  DocumentTypeDto,
  UpdateDocumentTypeDto,
} from '../../documents/document-type.models';

// Backend: Dignite.Paperbase.HttpApi.Documents.DocumentTypes.DocumentTypeController (/api/paperbase/document-types).
@Injectable({ providedIn: 'root' })
export class DocumentTypeService {
  private readonly rest = inject(RestService);
  private readonly apiName = 'Default';
  private readonly basePath = '/api/paperbase/document-types';

  // 当前层可见文档类型（Host admin → Host 类型；租户 admin → 自己租户类型；不跨层 union）。
  getVisible = (): Observable<DocumentTypeDto[]> =>
    this.rest.request<void, DocumentTypeDto[]>(
      { method: 'GET', url: this.basePath },
      { apiName: this.apiName },
    );

  // 当前层回收站（已软删除的私有类型）。
  getDeleted = (): Observable<DocumentTypeDto[]> =>
    this.rest.request<void, DocumentTypeDto[]>(
      { method: 'GET', url: `${this.basePath}/deleted` },
      { apiName: this.apiName },
    );

  create = (input: CreateDocumentTypeDto): Observable<DocumentTypeDto> =>
    this.rest.request<CreateDocumentTypeDto, DocumentTypeDto>(
      { method: 'POST', url: this.basePath, body: input },
      { apiName: this.apiName },
    );

  update = (id: string, input: UpdateDocumentTypeDto): Observable<DocumentTypeDto> =>
    this.rest.request<UpdateDocumentTypeDto, DocumentTypeDto>(
      { method: 'PUT', url: `${this.basePath}/${id}`, body: input },
      { apiName: this.apiName },
    );

  delete = (id: string): Observable<void> =>
    this.rest.request<void, void>(
      { method: 'DELETE', url: `${this.basePath}/${id}` },
      { apiName: this.apiName },
    );

  restore = (id: string): Observable<DocumentTypeDto> =>
    this.rest.request<void, DocumentTypeDto>(
      { method: 'POST', url: `${this.basePath}/${id}/restore` },
      { apiName: this.apiName },
    );
}
