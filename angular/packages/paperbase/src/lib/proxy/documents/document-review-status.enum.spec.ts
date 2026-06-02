import { describe, expect, it } from 'vitest';

import {
  DocumentReviewStatus,
  documentReviewStatusOptions,
} from './document-review-status.enum';

describe('DocumentReviewStatus (smoke)', () => {
  it('exposes the expected numeric values', () => {
    expect(DocumentReviewStatus.None).toBe(0);
    expect(DocumentReviewStatus.PendingReview).toBe(10);
    expect(DocumentReviewStatus.Reviewed).toBe(20);
  });

  it('maps every member to an option entry', () => {
    expect(documentReviewStatusOptions).toHaveLength(3);
  });
});
