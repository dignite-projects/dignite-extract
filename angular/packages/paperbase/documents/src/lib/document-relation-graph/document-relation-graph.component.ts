import { Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe } from '@abp/ng.core';
import {
  DocumentRelationEdgeDto,
  DocumentRelationGraphDto,
  DocumentRelationNodeDto,
  DocumentRelationService,
  DocumentLifecycleStatus,
  RelationSource,
} from '@dignite/paperbase';

interface PositionedNode extends DocumentRelationNodeDto {
  x: number;
  y: number;
  isRoot: boolean;
}

interface PositionedEdge {
  edge: DocumentRelationEdgeDto;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

/**
 * Wiki-style radial graph viewer for the DocumentRelation graph (Issue #115 follow-up,
 * the "navigability" half of the wiki concept). Consumes
 * {@link DocumentRelationService.getGraph} and renders a force-free SVG with the root
 * document at center and successive hops on concentric rings. Click a node to navigate
 * to that document's detail page.
 *
 * Why no force-directed layout: enterprise document graphs are typically small
 * (≤ 30 nodes within depth 3); a deterministic radial layout is easier to reason about,
 * easier to test, and adds zero runtime deps. Upgrade to d3-force / cytoscape only if
 * graph density grows.
 */
@Component({
  selector: 'lib-document-relation-graph',
  templateUrl: './document-relation-graph.component.html',
  styleUrls: ['./document-relation-graph.component.scss'],
  imports: [CommonModule, RouterModule, FormsModule, LocalizationPipe],
})
export class DocumentRelationGraphComponent implements OnInit {
  @Input({ required: true }) documentId!: string;

  private readonly relationService = inject(DocumentRelationService);
  private readonly router = inject(Router);

  readonly RelationSource = RelationSource;
  readonly DocumentLifecycleStatus = DocumentLifecycleStatus;

  readonly viewBoxSize = 800;
  readonly ringRadiusStep = 130;

  graph = signal<DocumentRelationGraphDto | null>(null);
  isLoading = signal(false);
  error = signal<string | null>(null);

  depth = signal<number>(2);
  includeAiSuggested = signal<boolean>(true);
  hoveredEdge = signal<DocumentRelationEdgeDto | null>(null);
  hoveredNode = signal<PositionedNode | null>(null);

  nodes = computed<PositionedNode[]>(() => {
    const g = this.graph();
    if (!g || !g.nodes?.length) return [];

    // Group nodes by hop distance — root (distance 0) sits at center, the rest
    // distribute evenly around concentric rings so siblings at the same hop
    // distance share a ring.
    const byDistance = new Map<number, DocumentRelationNodeDto[]>();
    for (const n of g.nodes) {
      const d = n.distance ?? 0;
      const bucket = byDistance.get(d) ?? [];
      bucket.push(n);
      byDistance.set(d, bucket);
    }

    const positioned: PositionedNode[] = [];
    for (const [distance, group] of byDistance) {
      if (distance === 0) {
        // Root document — always at the origin.
        for (const n of group) {
          positioned.push({ ...n, x: 0, y: 0, isRoot: true });
        }
        continue;
      }

      const radius = distance * this.ringRadiusStep;
      const count = group.length;
      // Offset by distance * 0.3 radians so different rings don't all line up
      // on the same vertical axis when there's only 1–2 nodes per ring.
      const angleOffset = distance * 0.3;
      group.forEach((n, idx) => {
        const angle = angleOffset + (idx / count) * Math.PI * 2;
        positioned.push({
          ...n,
          x: Math.cos(angle) * radius,
          y: Math.sin(angle) * radius,
          isRoot: false,
        });
      });
    }
    return positioned;
  });

  edges = computed<PositionedEdge[]>(() => {
    const g = this.graph();
    if (!g || !g.edges?.length) return [];

    const positionByDocId = new Map<string, PositionedNode>();
    for (const n of this.nodes()) {
      positionByDocId.set(n.documentId, n);
    }

    const result: PositionedEdge[] = [];
    for (const edge of g.edges) {
      const source = positionByDocId.get(edge.sourceDocumentId);
      const target = positionByDocId.get(edge.targetDocumentId);
      // Defensive: an edge can in principle reference a node outside the BFS
      // frontier (if the backend returns it without the corresponding node);
      // skip rather than draw to (0,0).
      if (!source || !target) continue;
      result.push({
        edge,
        x1: source.x,
        y1: source.y,
        x2: target.x,
        y2: target.y,
      });
    }
    return result;
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.isLoading.set(true);
    this.error.set(null);
    this.relationService
      .getGraph({
        rootDocumentId: this.documentId,
        depth: this.depth(),
        includeAiSuggested: this.includeAiSuggested(),
      })
      .subscribe({
        next: g => {
          this.graph.set(g);
          this.isLoading.set(false);
        },
        error: () => {
          this.error.set('::Document:RelationGraph:LoadFailed');
          this.isLoading.set(false);
        },
      });
  }

  setDepth(d: number): void {
    if (d < 1 || d > 3 || d === this.depth()) return;
    this.depth.set(d);
    this.load();
  }

  toggleAiSuggested(): void {
    this.includeAiSuggested.update(v => !v);
    this.load();
  }

  navigateToNode(node: PositionedNode): void {
    if (node.isRoot) return; // Already on this document's page.
    this.router.navigate(['/documents', node.documentId]);
  }

  edgeStrokeClass(edge: DocumentRelationEdgeDto): string {
    switch (edge.source) {
      case RelationSource.Manual:      return 'edge-manual';
      case RelationSource.AiSuggested: return 'edge-ai';
      case RelationSource.ModuleAuto:  return 'edge-module';
      default:                         return 'edge-default';
    }
  }

  nodeFillClass(node: PositionedNode): string {
    if (node.isRoot) return 'node-root';
    switch (node.lifecycleStatus) {
      case DocumentLifecycleStatus.Ready:      return 'node-ready';
      case DocumentLifecycleStatus.Processing: return 'node-processing';
      case DocumentLifecycleStatus.Failed:     return 'node-failed';
      default:                                 return 'node-default';
    }
  }

  /** Truncate node label so it doesn't overflow the SVG. */
  shortLabel(text: string | undefined | null, max = 20): string {
    if (!text) return '—';
    return text.length > max ? text.substring(0, max - 1) + '…' : text;
  }
}
