import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { finalize } from 'rxjs';
import {
  ContractDto,
  ContractReviewStatus,
  ContractStatus,
  ContractsService,
  UpdateContractDto,
} from '../services/contracts.service';

interface ContractFormState {
  title: string;
  contractNumber: string;
  partyAName: string;
  partyBName: string;
  signedDate: string;
  effectiveDate: string;
  expirationDate: string;
  totalAmount: number | null;
  currency: string;
  autoRenewal: boolean | null;
  terminationNoticeDays: number | null;
  governingLaw: string;
  summary: string;
}

@Component({
  selector: 'lib-contract-detail',
  imports: [CommonModule, FormsModule, RouterModule, LocalizationPipe],
  templateUrl: './contract-detail.component.html',
  styleUrls: ['./contract-detail.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContractDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly service = inject(ContractsService);
  private readonly toaster = inject(ToasterService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly destroyRef = inject(DestroyRef);

  readonly ContractStatus = ContractStatus;
  readonly ContractReviewStatus = ContractReviewStatus;

  readonly contract = signal<ContractDto | null>(null);
  readonly form = signal<ContractFormState>(EMPTY_FORM);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly confirming = signal(false);

  readonly canConfirm = computed(() => {
    const c = this.contract();
    return !!c && c.needsReview;
  });

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.toaster.error('Contracts::Contract:NotFound', 'AbpUi::Error');
      this.router.navigate(['..'], { relativeTo: this.route });
      return;
    }

    this.load(id);
  }

  back(): void {
    this.router.navigate(['..'], { relativeTo: this.route });
  }

  save(): void {
    const c = this.contract();
    if (!c?.id || this.saving()) return;

    this.saving.set(true);
    this.service
      .update(c.id, this.toUpdateDto(this.form()))
      .pipe(
        finalize(() => this.saving.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: updated => {
          this.applyContract(updated);
          this.toaster.success('Contracts::Contract:UpdateSuccess', 'AbpUi::Success');
        },
        error: () => this.toaster.error('Contracts::Contract:UpdateFailed', 'AbpUi::Error'),
      });
  }

  confirm(): void {
    const c = this.contract();
    if (!c?.id || this.confirming()) return;

    this.confirmation
      .info('Contracts::Contract:ConfirmExtraction', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;

        this.confirming.set(true);
        this.service
          .confirm(c.id)
          .pipe(
            finalize(() => this.confirming.set(false)),
            takeUntilDestroyed(this.destroyRef),
          )
          .subscribe({
            next: () => {
              // Re-fetch to get updated NeedsReview / ReviewStatus / Status from server.
              this.load(c.id);
              this.toaster.success('Contracts::Contract:ConfirmSuccess', 'AbpUi::Success');
            },
            error: () => this.toaster.error('Contracts::Contract:ConfirmFailed', 'AbpUi::Error'),
          });
      });
  }

  reviewStatusBadgeClass(status: ContractReviewStatus): string {
    switch (status) {
      case ContractReviewStatus.Pending:
        return 'badge text-bg-warning';
      case ContractReviewStatus.Confirmed:
        return 'badge text-bg-success';
      case ContractReviewStatus.Corrected:
        return 'badge text-bg-info';
      default:
        return 'badge text-bg-secondary';
    }
  }

  reviewStatusLabel(status: ContractReviewStatus): string {
    switch (status) {
      case ContractReviewStatus.Pending:
        return 'Contracts::ContractReviewStatus:Pending';
      case ContractReviewStatus.Confirmed:
        return 'Contracts::ContractReviewStatus:Confirmed';
      case ContractReviewStatus.Corrected:
        return 'Contracts::ContractReviewStatus:Corrected';
      default:
        return '-';
    }
  }

  statusLabel(status: ContractStatus): string {
    return ContractStatus[status] ?? '-';
  }

  private load(id: string): void {
    this.loading.set(true);
    this.service
      .get(id)
      .pipe(
        finalize(() => this.loading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: dto => this.applyContract(dto),
        error: () => {
          this.toaster.error('Contracts::Contract:LoadFailed', 'AbpUi::Error');
          this.router.navigate(['..'], { relativeTo: this.route });
        },
      });
  }

  private applyContract(dto: ContractDto): void {
    this.contract.set(dto);
    this.form.set({
      title: dto.title ?? '',
      contractNumber: dto.contractNumber ?? '',
      partyAName: dto.partyAName ?? '',
      partyBName: dto.partyBName ?? '',
      signedDate: toInputDate(dto.signedDate),
      effectiveDate: toInputDate(dto.effectiveDate),
      expirationDate: toInputDate(dto.expirationDate),
      totalAmount: dto.totalAmount ?? null,
      currency: dto.currency ?? '',
      autoRenewal: dto.autoRenewal ?? null,
      terminationNoticeDays: dto.terminationNoticeDays ?? null,
      governingLaw: dto.governingLaw ?? '',
      summary: dto.summary ?? '',
    });
  }

  private toUpdateDto(form: ContractFormState): UpdateContractDto {
    return {
      title: emptyToNull(form.title),
      contractNumber: emptyToNull(form.contractNumber),
      partyAName: emptyToNull(form.partyAName),
      partyBName: emptyToNull(form.partyBName),
      signedDate: emptyToNull(form.signedDate),
      effectiveDate: emptyToNull(form.effectiveDate),
      expirationDate: emptyToNull(form.expirationDate),
      totalAmount: form.totalAmount,
      currency: emptyToNull(form.currency),
      autoRenewal: form.autoRenewal,
      terminationNoticeDays: form.terminationNoticeDays,
      governingLaw: emptyToNull(form.governingLaw),
      summary: emptyToNull(form.summary),
    };
  }

  patch<K extends keyof ContractFormState>(key: K, value: ContractFormState[K]): void {
    this.form.update(f => ({ ...f, [key]: value }));
  }
}

const EMPTY_FORM: ContractFormState = {
  title: '',
  contractNumber: '',
  partyAName: '',
  partyBName: '',
  signedDate: '',
  effectiveDate: '',
  expirationDate: '',
  totalAmount: null,
  currency: '',
  autoRenewal: null,
  terminationNoticeDays: null,
  governingLaw: '',
  summary: '',
};

function toInputDate(value?: string): string {
  if (!value) return '';
  // Backend returns ISO timestamp; <input type="date"> needs yyyy-MM-dd.
  return value.length >= 10 ? value.substring(0, 10) : value;
}

function emptyToNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}
