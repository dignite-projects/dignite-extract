import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { map } from 'rxjs';
import {
  CreateFieldDefinitionDto,
  DocumentTypeService,
  FieldDataType,
  FieldDefinitionDto,
  FieldDefinitionService,
  fieldDataTypeOptions,
  PAPERBASE_PERMISSIONS,
  SlugSuggestionService,
} from '@dignite/paperbase';
import { SlugSuggestionHandle, wireSlugSuggestion } from '../../shared/slug-suggestion';

// Mirrors FieldDefinitionConsts (Domain.Shared): Name whitelist + length caps.
const NAME_PATTERN = /^[A-Za-z0-9_\-]{1,64}$/;
const MAX_NAME_LENGTH = 64;
const MAX_DISPLAY_NAME_LENGTH = 128;
const MAX_PROMPT_LENGTH = 1024;

@Component({
  selector: 'lib-field-definition-list',
  templateUrl: './field-definition-list.component.html',
  styleUrls: ['./field-definition-list.component.scss'],
  imports: [CommonModule, ReactiveFormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FieldDefinitionListComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly service = inject(FieldDefinitionService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly slugService = inject(SlugSuggestionService);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  // Create/edit/delete buttons require any FieldDefinitions write grant (#217); the route's
  // FieldDefinitions.Default only lists. ABP evaluates the `||` policy expression.
  readonly canManage = this.permissionService.getGrantedPolicy(
    `${PAPERBASE_PERMISSIONS.FieldDefinitions.Create} || ${PAPERBASE_PERMISSIONS.FieldDefinitions.Update} || ${PAPERBASE_PERMISSIONS.FieldDefinitions.Delete}`,
  );
  readonly dataTypeOptions = fieldDataTypeOptions;
  readonly FieldDataType = FieldDataType;

  // 路由按不可变 DocumentTypeId 绑定（#207）；header 徽标主显示用户友好的 DisplayName（#261），
  // TypeCode 降为 hover 提示——二者均由当前层可见类型按 Id 即时解析（穿透重命名）。
  documentTypeId = '';
  documentTypeDisplayName = signal('');
  documentTypeCode = signal('');
  fields = signal<FieldDefinitionDto[]>([]);
  isLoading = signal(true);
  showDeleted = signal(false);

  editing = signal<FieldDefinitionDto | 'create' | null>(null);
  isSubmitting = signal(false);
  isSuggesting = signal(false);

  private slugHandle?: SlugSuggestionHandle;

  readonly form = this.fb.nonNullable.group({
    name: [
      '',
      [Validators.required, Validators.maxLength(MAX_NAME_LENGTH), Validators.pattern(NAME_PATTERN)],
    ],
    displayName: ['', [Validators.required, Validators.maxLength(MAX_DISPLAY_NAME_LENGTH)]],
    // 抽取指令选填（实测反馈）：去掉 Validators.required，仅保留长度上限；留空时后端 NormalizePrompt 收敛为 null。
    prompt: ['', [Validators.maxLength(MAX_PROMPT_LENGTH)]],
    dataType: [FieldDataType.Text, [Validators.required]],
    displayOrder: [0, [Validators.required]],
    isRequired: [false],
    // #212：多值仅文本有效（镜像后端 FieldDefinition.ValidateMultiValue 不变量）。
    // 非文本时由 applyAllowMultiplePolicy 强制置 false 并 disable，提交前 getRawValue 仍带回 false。
    allowMultiple: [false],
  });

  // 驱动模板：dataType === Text 时才允许勾选"多值"。
  readonly isTextType = signal(true);

  ngOnInit(): void {
    this.documentTypeId = this.route.snapshot.paramMap.get('typeId') ?? '';
    this.resolveDocumentType();
    this.slugHandle = wireSlugSuggestion({
      displayName: this.form.controls.displayName,
      target: this.form.controls.name,
      suggest: text => this.slugService.suggest({ label: text }, undefined).pipe(map(r => r.slug ?? '')),
      fallback: () => this.nextFieldSlug(),
      destroyRef: this.destroyRef,
      onPending: pending => this.isSuggesting.set(pending),
    });
    // #212：dataType 变化时实时套用"多值仅文本"策略（镜像后端不变量，避免提交非法组合被后端 loud fail）。
    this.form.controls.dataType.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(dataType => this.applyAllowMultiplePolicy(dataType));
    this.load();
  }

  // 非文本字段强制 allowMultiple=false 且禁用勾选框；切回文本时重新启用（保留当前值）。
  // 仅文本 + 多值是后端实体层允许的组合（FieldDefinition.MultiValueRequiresStringType），客户端镜像该约束做 UX 防呆。
  private applyAllowMultiplePolicy(dataType: FieldDataType): void {
    const isText = dataType === FieldDataType.Text;
    this.isTextType.set(isText);
    const control = this.form.controls.allowMultiple;
    if (isText) {
      control.enable({ emitEvent: false });
    } else {
      control.setValue(false, { emitEvent: false });
      control.disable({ emitEvent: false });
    }
  }

  // header 徽标展示用：按不可变 Id 在当前层可见类型里解析当前类型，主显示 DisplayName、TypeCode 作 hover 提示（穿透重命名）。
  private resolveDocumentType(): void {
    this.documentTypeService.getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: types => {
          const type = types.find(t => t.id === this.documentTypeId);
          this.documentTypeDisplayName.set(type?.displayName ?? '');
          this.documentTypeCode.set(type?.typeCode ?? '');
        },
      });
  }

  // LLM 不可用 / 未翻译时的本地回退：取与现有字段名不冲突的最小 field_{n}。
  private nextFieldSlug(): string {
    const existing = new Set(this.fields().map(f => f.name));
    let i = 1;
    while (existing.has(`field_${i}`)) i++;
    return `field_${i}`;
  }

  refresh(): void {
    this.load();
  }

  toggleDeleted(): void {
    this.showDeleted.update(v => !v);
    this.load();
  }

  goBack(): void {
    this.router.navigate(['/documents/types']);
  }

  private load(): void {
    this.isLoading.set(true);
    const source$ = this.service.getList({
      documentTypeId: this.documentTypeId,
      onlyDeleted: this.showDeleted(),
    });
    source$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => {
        this.fields.set([...list].sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0)));
        this.isLoading.set(false);
      },
      error: () => this.isLoading.set(false),
    });
  }

  openCreate(): void {
    const nextOrder = this.fields().reduce((max, f) => Math.max(max, f.displayOrder ?? 0), -1) + 1;
    this.form.reset({
      name: '',
      displayName: '',
      prompt: '',
      dataType: FieldDataType.Text,
      displayOrder: nextOrder,
      isRequired: false,
      allowMultiple: false,
    });
    this.form.controls.name.enable();
    this.applyAllowMultiplePolicy(FieldDataType.Text);
    // 必须在 form.reset()/enable() 之后调用：二者触发的 valueChanges 会误标"手动编辑"，
    // reset() 清掉该标记并复位建议状态（含 spinner）。
    this.slugHandle?.reset();
    this.editing.set('create');
  }

  openEdit(field: FieldDefinitionDto): void {
    // 先 disable 再 reset：让 slug 自动建议在编辑态 reset 期间识别为非自动接管，
    // 不会把既有 name 当"过期键"清空（见 wireSlugSuggestion 注释）。
    this.form.controls.name.disable();
    this.form.reset({
      name: field.name,
      displayName: field.displayName,
      prompt: field.prompt ?? '',
      dataType: field.dataType,
      displayOrder: field.displayOrder,
      isRequired: field.isRequired,
      allowMultiple: field.allowMultiple,
    });
    this.form.controls.name.enable();
    this.applyAllowMultiplePolicy(field.dataType ?? FieldDataType.Text);
    this.slugHandle?.markManual();
    this.editing.set(field);
  }

  // 显示名失焦 → 触发 slug 自动建议（实测反馈：从停顿防抖改为失焦触发）。
  onDisplayNameBlur(): void {
    this.slugHandle?.notifyDisplayNameBlur();
  }

  // 遮罩关闭防误触：只有当 mousedown 与 click 都发生在遮罩本身（而非对话框内）时才关闭。
  // 否则在输入框里拖选文本、松手落在遮罩区时，浏览器会在遮罩上触发 click（mousedown/mouseup 的最近公共祖先），
  // 误关弹窗并丢失已填内容。记录 mousedown 起点是判定"这一次点击是否真的从遮罩发起"的唯一可靠方式。
  private backdropMouseDownOnSelf = false;

  onBackdropMouseDown(event: MouseEvent): void {
    this.backdropMouseDownOnSelf = event.target === event.currentTarget;
  }

  onBackdropClick(event: MouseEvent): void {
    if (this.backdropMouseDownOnSelf && event.target === event.currentTarget) {
      this.closeModal();
    }
    this.backdropMouseDownOnSelf = false;
  }

  closeModal(): void {
    this.editing.set(null);
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const mode = this.editing();
    if (mode === null) return;

    this.isSubmitting.set(true);
    const raw = this.form.getRawValue();

    if (mode === 'create') {
      const input: CreateFieldDefinitionDto = {
        documentTypeId: this.documentTypeId,
        name: raw.name,
        displayName: raw.displayName,
        prompt: raw.prompt,
        dataType: raw.dataType,
        displayOrder: raw.displayOrder,
        isRequired: raw.isRequired,
        // 非文本时 control 被 disable，但 getRawValue 仍带回（已被策略置 false）。
        allowMultiple: raw.allowMultiple,
      };
      this.service.create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::FieldDefinition:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service.update(mode.id!, {
        name: raw.name,
        displayName: raw.displayName,
        prompt: raw.prompt,
        dataType: raw.dataType,
        displayOrder: raw.displayOrder,
        isRequired: raw.isRequired,
        allowMultiple: raw.allowMultiple,
      })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::FieldDefinition:UpdatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    }
  }

  private onSaved(messageKey: string): void {
    this.isSubmitting.set(false);
    this.closeModal();
    this.toaster.success(messageKey, '::Success');
    this.load();
  }

  delete(field: FieldDefinitionDto): void {
    this.confirmation
      .warn('::FieldDefinition:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.service.delete(field.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::FieldDefinition:DeletedSuccessfully', '::Success');
              this.load();
            },
            error: () => this.toaster.error('::FieldDefinition:DeleteFailed', '::Error'),
          });
      });
  }

  restore(field: FieldDefinitionDto): void {
    this.service.restore(field.id!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.toaster.success('::FieldDefinition:RestoredSuccessfully', '::Success');
          this.load();
        },
      });
  }

  dataTypeLabel(dataType: FieldDataType | undefined): string {
    return this.dataTypeOptions.find(o => o.value === dataType)?.key ?? String(dataType);
  }
}
