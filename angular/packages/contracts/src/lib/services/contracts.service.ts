import { EnvironmentService, RestService } from '@abp/ng.core';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface ContractDto {
  id: string;
  documentId: string;
  documentTypeCode: string;
  title?: string;
  contractNumber?: string;
  partyAName?: string;
  partyBName?: string;
  signedDate?: string;
  effectiveDate?: string;
  expirationDate?: string;
  totalAmount?: number;
  currency?: string;
  autoRenewal?: boolean;
  terminationNoticeDays?: number;
  governingLaw?: string;
  summary?: string;
  status: ContractStatus;
  extractionConfidence?: number;
  needsReview: boolean;
  reviewStatus: ContractReviewStatus;
}

export interface UpdateContractDto {
  title?: string | null;
  contractNumber?: string | null;
  partyAName?: string | null;
  partyBName?: string | null;
  signedDate?: string | null;
  effectiveDate?: string | null;
  expirationDate?: string | null;
  totalAmount?: number | null;
  currency?: string | null;
  autoRenewal?: boolean | null;
  terminationNoticeDays?: number | null;
  governingLaw?: string | null;
  summary?: string | null;
}

export enum ContractStatus {
  Draft = 0,
  Active = 1,
  Expired = 2,
  Terminated = 3,
  Archived = 4,
}

export enum ContractReviewStatus {
  Pending = 0,
  Confirmed = 1,
  Corrected = 2,
}

export interface GetContractListInput {
  skipCount?: number;
  maxResultCount?: number;
  sorting?: string;
  documentId?: string;
  expirationDateFrom?: string;
  expirationDateTo?: string;
  needsReview?: boolean;
  reviewStatus?: ContractReviewStatus;
  amountMin?: number;
  amountMax?: number;
}

export interface PagedResultDto<T> {
  items: T[];
  totalCount: number;
}

@Injectable({
  providedIn: 'root',
})
export class ContractsService {
  private readonly apiName = 'Contracts';

  private readonly restService = inject(RestService);
  private readonly env = inject(EnvironmentService);

  getList(input: GetContractListInput): Observable<PagedResultDto<ContractDto>> {
    const params = {
      ...input,
      totalAmountMin: input.amountMin,
      totalAmountMax: input.amountMax,
      amountMin: undefined,
      amountMax: undefined,
    };

    return this.restService.request<void, PagedResultDto<ContractDto>>(
      {
        method: 'GET',
        url: '/api/paperbase/contracts',
        params: params as Record<string, string | number | boolean | undefined>,
      },
      { apiName: this.apiName }
    );
  }

  get(id: string): Observable<ContractDto> {
    return this.restService.request<void, ContractDto>(
      {
        method: 'GET',
        url: `/api/paperbase/contracts/${id}`,
      },
      { apiName: this.apiName }
    );
  }

  update(id: string, input: UpdateContractDto): Observable<ContractDto> {
    return this.restService.request<UpdateContractDto, ContractDto>(
      {
        method: 'PUT',
        url: `/api/paperbase/contracts/${id}`,
        body: input,
      },
      { apiName: this.apiName }
    );
  }

  confirm(id: string): Observable<void> {
    return this.restService.request<void, void>(
      {
        method: 'POST',
        url: `/api/paperbase/contracts/${id}/confirm`,
      },
      { apiName: this.apiName }
    );
  }

  getExportUrl(input?: GetContractListInput): string {
    const params = new URLSearchParams();
    if (input?.expirationDateFrom) params.set('expirationDateFrom', input.expirationDateFrom);
    if (input?.expirationDateTo) params.set('expirationDateTo', input.expirationDateTo);
    if (input?.reviewStatus != null) params.set('reviewStatus', String(input.reviewStatus));
    if (input?.amountMin != null) params.set('totalAmountMin', String(input.amountMin));
    if (input?.amountMax != null) params.set('totalAmountMax', String(input.amountMax));
    const qs = params.toString();
    return `${this.env.getApiUrl(undefined)}/api/paperbase/contracts/export${qs ? '?' + qs : ''}`;
  }
}
