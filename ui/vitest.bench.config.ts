/// <reference types="vitest/config" />
// Micro-benchmarks (Vitest 5 `vitest bench` + happy-dom): ui/bench/**/*.bench.ts. Run with `npm run bench`;
// `npm run bench:ci` also writes bench-results/ui-bench.json (the json reporter, compared by scripts/bench-compare.mjs).
// Separate from vitest.config.ts so `npm test` / coverage never pick up a bench file. See CONTRIBUTING.md "Benchmarks".
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "happy-dom",
    include: [],
    benchmark: {
      include: ["bench/**/*.bench.ts"],
      // Vitest's module runner exposes every export through a getter, so calls across modules (reactive.ts →
      // scheduler.ts) cost a little more than in the bundled app, and Vitest warns about it. The overhead is the same
      // in every run, so before/after comparisons stay valid; absolute numbers are a bit pessimistic.
      suppressExportGetterWarnings: true,
    },
    // one bench file at a time: parallel workers would compete for the same cores and skew each other's numbers
    fileParallelism: false,
    // a test holds a whole comparison group (several tasks × time budget)
    testTimeout: 300_000,
    hookTimeout: 120_000,
    // the default reporter collapses passed tests; verbose prints every comparison table
    reporters: ["verbose"],
  },
});
