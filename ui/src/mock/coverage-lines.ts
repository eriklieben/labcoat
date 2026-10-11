// COVERAGE mock: GET /api/coverage/line ("Which tests cover this line?", feature F1) for mock mode.
// Exact entries come from the mock's per-test coverage cache; approximate ones fake an impact map: a few tests of the
// projects whose full-run coverage executes the line, picked deterministically per line.
import type { CoverageReport, CoveringTest, CoveringTests, TestNode } from "../contract";
import { makeCoverage } from "./coverage";

function hash(s: string) {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
  return Math.abs(h);
}

const lineHits = (r: CoverageReport | undefined, path: string, line: number) =>
  r?.files.find((f) => f.path === path)?.lines.find((l) => l.line === line)?.hits ?? 0;
const hitsLine = (r: CoverageReport | undefined, path: string, line: number) => lineHits(r, path, line) > 0;

export function mockCoverageLine(path: string, line: number, tests: TestNode[], perTest: Iterable<CoverageReport>, signal?: AbortSignal): Promise<CoveringTests> {
  return new Promise((resolve, reject) => {
    const id = window.setTimeout(() => {
      const out: CoveringTest[] = [];
      const seen = new Set<string>();
      const byId = new Map(tests.map((t) => [`${t.projectId}|${t.id}`, t]));
      for (const r of perTest) {
        if (!hitsLine(r, path, line)) continue;
        for (const tid of r.testIds ?? []) {
          const k = `${r.projectIds[0]}|${tid}`;
          if (seen.has(k)) continue;
          seen.add(k);
          if (!byId.has(k)) continue;
          out.push({ projectId: r.projectIds[0], id: tid, source: "perTest", hits: lineHits(r, path, line), ...(r.stale ? { stale: true } : {}) });
        }
      }
      // Impact map: tests of every project whose full run executes this line.
      const projects = [...new Set(tests.map((t) => t.projectId))];
      const full = makeCoverage("mock-line", projects, 1);
      if (hitsLine(full, path, line)) {
        const owners = new Set(full!.projectIds.filter((p) => makeCoverage("mock-line", [p], 1)?.files.some((f) => f.path === path)));
        const candidates = tests.filter((t) => owners.has(t.projectId) && !seen.has(`${t.projectId}|${t.id}`));
        const n = Math.min(candidates.length, 1 + (hash(path + line) % 4));
        const start = candidates.length ? hash(`${path}#${line}`) % candidates.length : 0;
        for (let i = 0; i < n; i++) {
          const t = candidates[(start + i * 3) % candidates.length];
          const k = `${t.projectId}|${t.id}`;
          if (seen.has(k)) continue;
          seen.add(k);
          out.push({ projectId: t.projectId, id: t.id, source: "impactMap" });
        }
      }
      resolve({ tests: out, partial: true });
    }, 120);
    signal?.addEventListener("abort", () => {
      window.clearTimeout(id);
      reject(new DOMException("Aborted", "AbortError"));
    });
  });
}
