/// <reference types="vitest/config" />
// Unit tests (Vitest + happy-dom). Tests live under ui/test/, mirroring the source tree:
//   test/design-system/**  → design-system/src/**   (core, components, themes)
//   test/app/**            → src/**                 (store, transport, tree, components, …)
// Shared setup (browser API stubs, fake WebSocket, rAF control): test/setup.ts; helpers: test/helpers/.
// The Playwright checks (`npm run check:*`) stay as the end-to-end layer.
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "happy-dom",
    include: ["test/**/*.test.ts"],
    setupFiles: ["test/setup.ts"],
    restoreMocks: true,
    unstubGlobals: true,
    coverage: {
      provider: "v8",
      include: ["src/**/*.ts", "design-system/src/**/*.ts"],
      exclude: [
        // dev-only in-browser mock backend, loaded only with ?mock (never in production; tests use fakes instead)
        "src/mock/**",
        // bootstrap: reads location, creates the real WsTransport and mounts <lc-app> into #root (covered by the e2e checks)
        "src/main.ts",
        // type declarations only
        "**/*.d.ts",
        // micro-benchmarks (vitest.bench.config.ts, `npm run bench`): never part of the unit tests or their coverage
        "bench/**",
        "**/*.bench.ts",
      ],
      // lcovonly: coverage/lcov.info, imported by the SonarQube Cloud job in ci.yml (sonar); doesn't change the thresholds
      reporter: ["text", "text-summary", "json-summary", "html", "lcovonly"],
      reportsDirectory: "coverage",
      thresholds: {
        lines: 85,
        statements: 85,
        functions: 85,
        // per package (app, design system): each must hold the bar on its own
        "src/**/*.ts": { lines: 85, statements: 85, functions: 85 },
        "design-system/src/**/*.ts": { lines: 85, statements: 85, functions: 85 },
      },
    },
  },
});
