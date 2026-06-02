import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./vitest.setup.ts'],
    include: ['packages/**/*.spec.ts', 'apps/**/*.spec.ts'],
    // Skip the broken `@angular/build:unit-test` (vitest) builder; see vitest.setup.ts.
    exclude: ['**/node_modules/**', '**/dist/**', '**/.angular/**'],
  },
});
