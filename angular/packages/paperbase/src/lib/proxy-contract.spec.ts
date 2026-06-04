import { describe, expect, it } from 'vitest';

import {
  DocumentReviewStatus,
  documentReviewStatusOptions,
} from './proxy/documents/document-review-status.enum';
import { DocumentLifecycleStatus } from './proxy/documents/document-lifecycle-status.enum';
import { PipelineRunStatus } from './proxy/documents/pipelines/pipeline-run-status.enum';

// Contract smoke test for generator-produced enums (`nx g @abp/ng.schematics:proxy-add`).
// Lives OUTSIDE proxy/ so it survives regeneration (the generator overwrites proxy/ and
// emits no specs). Guards that the numeric values the frontend renders/branches on stay in
// sync with the backend Domain.Shared enums — a renumbered or dropped member fails here
// loudly instead of silently mis-rendering a badge or mis-gating an action.
describe('proxy enum contract (smoke)', () => {
  it('DocumentReviewStatus matches backend values', () => {
    expect(DocumentReviewStatus.None).toBe(0);
    expect(DocumentReviewStatus.PendingReview).toBe(10);
    expect(DocumentReviewStatus.Reviewed).toBe(20);
    expect(DocumentReviewStatus.Rejected).toBe(30);
    expect(documentReviewStatusOptions).toHaveLength(4);
  });

  it('DocumentLifecycleStatus matches backend values', () => {
    expect(DocumentLifecycleStatus.Uploaded).toBe(10);
    expect(DocumentLifecycleStatus.Processing).toBe(20);
    expect(DocumentLifecycleStatus.Ready).toBe(30);
    expect(DocumentLifecycleStatus.Failed).toBe(99);
  });

  it('PipelineRunStatus matches backend values', () => {
    expect(PipelineRunStatus.Pending).toBe(10);
    expect(PipelineRunStatus.Running).toBe(20);
    expect(PipelineRunStatus.Succeeded).toBe(30);
    expect(PipelineRunStatus.Failed).toBe(90);
    expect(PipelineRunStatus.Skipped).toBe(95);
  });
});
