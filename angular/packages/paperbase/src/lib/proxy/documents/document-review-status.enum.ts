import { mapEnumToOptions } from '@abp/ng.core';

export enum DocumentReviewStatus {
  None = 0,
  PendingReview = 10,
  Reviewed = 20,
  Rejected = 30,
}

export const documentReviewStatusOptions = mapEnumToOptions(DocumentReviewStatus);
