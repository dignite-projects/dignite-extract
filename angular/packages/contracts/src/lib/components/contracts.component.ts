import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { LocalizationPipe } from '@abp/ng.core';
import { finalize } from 'rxjs';
import {
  ContractDto,
  ContractReviewStatus,
  ContractStatus,
  ContractsService,
} from '../services/contracts.service';

@Component({
  selector: 'lib-contracts',
  imports: [CommonModule, FormsModule, RouterModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: [
    `
      .contract-row { cursor: pointer; }
      .contract-row:hover { background-color: var(--bs-table-hover-bg, rgba(0, 0, 0, 0.04)); }
    `,
  ],
  template: `
    <div class="container-fluid py-3">
      <div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-3">
        <h1 class="h3 mb-0">{{ 'Contracts::DocumentType:Contract' | abpLocalization }}</h1>
        <div class="d-flex gap-2">
          <button type="button" class="btn btn-outline-success" (click)="exportCsv()">
            <i class="fas fa-download me-1"></i>
            {{ 'Contracts::Contract:ExportCsv' | abpLocalization }}
          </button>
          <button type="button" class="btn btn-outline-primary" (click)="load()" [disabled]="loading()">
            <i class="fas fa-refresh me-1"></i>
            {{ 'AbpUi::Refresh' | abpLocalization }}
          </button>
        </div>
      </div>

      <div class="row g-2 align-items-end mb-3">
        <div class="col-6 col-md-2">
          <label class="form-label" for="expirationDateFrom">
            {{ 'Contracts::ExpirationDateFrom' | abpLocalization }}
          </label>
          <input
            id="expirationDateFrom"
            class="form-control"
            type="date"
            name="expirationDateFrom"
            [ngModel]="expirationDateFrom()"
            (ngModelChange)="expirationDateFrom.set($event)"
          />
        </div>
        <div class="col-6 col-md-2">
          <label class="form-label" for="expirationDateTo">
            {{ 'Contracts::ExpirationDateTo' | abpLocalization }}
          </label>
          <input
            id="expirationDateTo"
            class="form-control"
            type="date"
            name="expirationDateTo"
            [ngModel]="expirationDateTo()"
            (ngModelChange)="expirationDateTo.set($event)"
          />
        </div>
        <div class="col-6 col-md-2">
          <label class="form-label" for="amountMin">
            {{ 'Contracts::TotalAmountMin' | abpLocalization }}
          </label>
          <input
            id="amountMin"
            class="form-control"
            type="number"
            name="amountMin"
            [ngModel]="amountMin()"
            (ngModelChange)="amountMin.set($event)"
            min="0"
          />
        </div>
        <div class="col-6 col-md-2">
          <label class="form-label" for="amountMax">
            {{ 'Contracts::TotalAmountMax' | abpLocalization }}
          </label>
          <input
            id="amountMax"
            class="form-control"
            type="number"
            name="amountMax"
            [ngModel]="amountMax()"
            (ngModelChange)="amountMax.set($event)"
            min="0"
          />
        </div>
        <div class="col-12 col-md-2">
          <label class="form-label" for="reviewStatus">
            {{ 'Contracts::ReviewStatus' | abpLocalization }}
          </label>
          <select
            id="reviewStatus"
            class="form-select"
            name="reviewStatus"
            [ngModel]="reviewStatusFilter()"
            (ngModelChange)="reviewStatusFilter.set($event)"
            (change)="load()"
          >
            <option [ngValue]="undefined">{{ 'AbpUi::All' | abpLocalization }}</option>
            <option [ngValue]="ContractReviewStatus.Pending">
              {{ 'Contracts::ContractReviewStatus:Pending' | abpLocalization }}
            </option>
            <option [ngValue]="ContractReviewStatus.Confirmed">
              {{ 'Contracts::ContractReviewStatus:Confirmed' | abpLocalization }}
            </option>
            <option [ngValue]="ContractReviewStatus.Corrected">
              {{ 'Contracts::ContractReviewStatus:Corrected' | abpLocalization }}
            </option>
          </select>
        </div>
        <div class="col-12 col-md-auto">
          <button type="button" class="btn btn-primary" (click)="load()" [disabled]="loading()">
            <i class="fas fa-search me-1"></i>
            {{ 'AbpUi::Search' | abpLocalization }}
          </button>
        </div>
      </div>

      <div class="table-responsive">
        <table class="table table-hover align-middle mb-0">
          <thead>
            <tr>
              <th>{{ 'Contracts::Title' | abpLocalization }}</th>
              <th>{{ 'Contracts::PartyBName' | abpLocalization }}</th>
              <th>{{ 'Contracts::SignedDate' | abpLocalization }}</th>
              <th>{{ 'Contracts::ExpirationDate' | abpLocalization }}</th>
              <th class="text-end">{{ 'Contracts::TotalAmount' | abpLocalization }}</th>
              <th>{{ 'Contracts::Status' | abpLocalization }}</th>
              <th>{{ 'Contracts::ReviewStatus' | abpLocalization }}</th>
              <th class="text-end">{{ 'Contracts::Confidence' | abpLocalization }}</th>
            </tr>
          </thead>
          <tbody>
            @if (loading()) {
              <tr>
                <td colspan="8" class="text-center py-4">
                  <span class="spinner-border spinner-border-sm me-2"></span>
                  {{ 'AbpUi::Loading' | abpLocalization }}
                </td>
              </tr>
            }
            @for (contract of contracts(); track contract.id) {
              <tr
                class="contract-row"
                role="button"
                tabindex="0"
                (click)="open(contract)"
                (keydown.enter)="open(contract)"
              >
                <td>
                  <div class="fw-semibold">{{ contract.title || '-' }}</div>
                  <div class="text-muted small">{{ contract.contractNumber || contract.documentId }}</div>
                </td>
                <td>{{ contract.partyBName || '-' }}</td>
                <td>{{ contract.signedDate | date: 'yyyy-MM-dd' }}</td>
                <td>{{ contract.expirationDate | date: 'yyyy-MM-dd' }}</td>
                <td class="text-end">
                  {{ contract.totalAmount === null ? '-' : (contract.totalAmount | number: '1.0-0') }}
                  @if (contract.totalAmount !== null) {
                    <span>{{ contract.currency || 'JPY' }}</span>
                  }
                </td>
                <td>
                  <span class="badge" [ngClass]="statusClass(contract)">
                    {{ statusText(contract.status) }}
                  </span>
                </td>
                <td>
                  <span class="badge" [ngClass]="reviewStatusClass(contract.reviewStatus)">
                    {{ reviewStatusLocalizationKey(contract.reviewStatus) | abpLocalization }}
                  </span>
                </td>
                <td class="text-end">
                  {{ contract.extractionConfidence === null ? '-' : (contract.extractionConfidence | percent: '1.0-0') }}
                </td>
              </tr>
            }
            @if (!loading() && contracts().length === 0) {
              <tr>
                <td colspan="8" class="text-center text-muted py-4">
                  {{ 'AbpUi::NoDataAvailable' | abpLocalization }}
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class ContractsComponent implements OnInit {
  protected readonly ContractReviewStatus = ContractReviewStatus;
  protected readonly service = inject(ContractsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  // signals are required for OnPush compatibility — the subscribe callbacks
  // below run outside of any Zone-tracked event handler, so plain field
  // assignments would not trigger change detection on this component.
  protected contracts = signal<ContractDto[]>([]);
  protected expirationDateFrom = signal('');
  protected expirationDateTo = signal('');
  protected amountMin = signal<number | null>(null);
  protected amountMax = signal<number | null>(null);
  protected reviewStatusFilter = signal<ContractReviewStatus | undefined>(undefined);
  protected loading = signal(false);

  ngOnInit(): void {
    this.load();
  }

  protected exportCsv(): void {
    const url = this.service.getExportUrl({
      expirationDateFrom: this.expirationDateFrom() || undefined,
      expirationDateTo: this.expirationDateTo() || undefined,
      amountMin: this.amountMin() ?? undefined,
      amountMax: this.amountMax() ?? undefined,
      reviewStatus: this.reviewStatusFilter(),
    });
    window.open(url, '_blank');
  }

  protected load(): void {
    this.loading.set(true);
    this.service
      .getList({
        skipCount: 0,
        maxResultCount: 20,
        sorting: 'expirationDate',
        expirationDateFrom: this.expirationDateFrom() || undefined,
        expirationDateTo: this.expirationDateTo() || undefined,
        amountMin: this.amountMin() ?? undefined,
        amountMax: this.amountMax() ?? undefined,
        reviewStatus: this.reviewStatusFilter(),
      })
      .pipe(
        finalize(() => this.loading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(result => {
        this.contracts.set(result.items);
      });
  }

  protected statusText(status: ContractStatus): string {
    return ContractStatus[status] ?? '-';
  }

  protected reviewStatusLocalizationKey(status: ContractReviewStatus): string {
    return `Contracts::ContractReviewStatus:${ContractReviewStatus[status] ?? 'Pending'}`;
  }

  protected statusClass(contract: ContractDto): string {
    return contract.status === ContractStatus.Active ? 'text-bg-success' : 'text-bg-secondary';
  }

  protected reviewStatusClass(status: ContractReviewStatus): string {
    switch (status) {
      case ContractReviewStatus.Pending:
        return 'text-bg-warning';
      case ContractReviewStatus.Corrected:
        return 'text-bg-info';
      case ContractReviewStatus.Confirmed:
        return 'text-bg-success';
      default:
        return 'text-bg-secondary';
    }
  }

  protected open(contract: ContractDto): void {
    this.router.navigate([contract.id], { relativeTo: this.route });
  }
}
