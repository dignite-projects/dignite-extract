import { defineConfig } from 'vitest/config';
import { fileURLToPath } from 'node:url';

const fromRoot = (relative: string) => fileURLToPath(new URL(relative, import.meta.url));

export default defineConfig({
  // Mirror the tsconfig.base.json `paths` so specs resolve `@dignite/vault-extract*` to the same source
  // the build/app use (vitest has no tsconfig-paths plugin). Anchored regex `$` so the primary entry
  // point's alias does not swallow the `/documents` and `/config` sub-entry-points.
  resolve: {
    alias: [
      {
        find: /^@dignite\/vault-extract\/documents$/,
        replacement: fromRoot('./packages/vault-extract/documents/src/public-api.ts'),
      },
      {
        find: /^@dignite\/vault-extract\/config$/,
        replacement: fromRoot('./packages/vault-extract/config/src/public-api.ts'),
      },
      {
        find: /^@dignite\/vault-extract$/,
        replacement: fromRoot('./packages/vault-extract/src/public-api.ts'),
      },
    ],
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./vitest.setup.ts'],
    include: ['packages/**/*.spec.ts', 'apps/**/*.spec.ts'],
    // Skip the broken `@angular/build:unit-test` (vitest) builder; see vitest.setup.ts.
    exclude: ['**/node_modules/**', '**/dist/**', '**/.angular/**'],
  },
});
