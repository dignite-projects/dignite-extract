import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { DOCUMENT_AI_PERMISSIONS } from '@dignite/document-ai';
import { DocumentUploadComponent } from '../document-upload/document-upload.component';

@Component({
  selector: 'lib-document-home',
  templateUrl: './document-home.component.html',
  styleUrls: ['./document-home.component.scss'],
  imports: [CommonModule, RouterModule, LocalizationPipe, DocumentUploadComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentHomeComponent {
  private readonly permissionService = inject(PermissionService);

  readonly canUpload = this.permissionService.getGrantedPolicy(
    DOCUMENT_AI_PERMISSIONS.Documents.Upload,
  );
  readonly canReview = this.permissionService.getGrantedPolicy(
    DOCUMENT_AI_PERMISSIONS.Documents.ConfirmClassification,
  );
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    DOCUMENT_AI_PERMISSIONS.Cabinets.Default,
  );
}
