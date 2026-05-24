import { Injectable, inject } from '@angular/core';
import { RestService } from '@abp/ng.core';
import { Observable } from 'rxjs';
import type {
  CreateExportTemplateDto,
  ExportDocumentsInput,
  ExportTemplateDto,
  UpdateExportTemplateDto,
} from '../../documents/export-template.models';

// Backend: ExportTemplateController (/api/paperbase/export-templates).
@Injectable({ providedIn: 'root' })
export class ExportTemplateService {
  private readonly rest = inject(RestService);
  private readonly apiName = 'Default';
  private readonly basePath = '/api/paperbase/export-templates';

  get = (id: string): Observable<ExportTemplateDto> =>
    this.rest.request<void, ExportTemplateDto>(
      { method: 'GET', url: `${this.basePath}/${id}` },
      { apiName: this.apiName },
    );

  getList = (): Observable<ExportTemplateDto[]> =>
    this.rest.request<void, ExportTemplateDto[]>(
      { method: 'GET', url: this.basePath },
      { apiName: this.apiName },
    );

  create = (input: CreateExportTemplateDto): Observable<ExportTemplateDto> =>
    this.rest.request<CreateExportTemplateDto, ExportTemplateDto>(
      { method: 'POST', url: this.basePath, body: input },
      { apiName: this.apiName },
    );

  update = (id: string, input: UpdateExportTemplateDto): Observable<ExportTemplateDto> =>
    this.rest.request<UpdateExportTemplateDto, ExportTemplateDto>(
      { method: 'PUT', url: `${this.basePath}/${id}`, body: input },
      { apiName: this.apiName },
    );

  delete = (id: string): Observable<void> =>
    this.rest.request<void, void>(
      { method: 'DELETE', url: `${this.basePath}/${id}` },
      { apiName: this.apiName },
    );

  // Returns a file stream (CSV / XLSX). responseType blob so the operator can download it.
  export = (input: ExportDocumentsInput): Observable<Blob> =>
    this.rest.request<ExportDocumentsInput, Blob>(
      { method: 'POST', url: `${this.basePath}/export`, body: input, responseType: 'blob' as 'json' },
      { apiName: this.apiName },
    );
}
