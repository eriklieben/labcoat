// Decides whether a green push to main cuts a release: yes when a pull request merged since the last release tag has
// the label `cut-release`, or `security` (Renovate's vulnerability fixes shouldn't wait for one). semantic-release then
// works out the version from every commit since that tag, so unlabeled merges go out together with the labeled one.
//
// Every commit since the tag is looked at, not only the pushed one: when a second merge lands while the first one's CI
// runs, only the newest commit's run releases, and that commit may not be the labeled one.
//
// Run by ci.yml (job `release`). A failed GitHub API call fails the job rather than skipping or starting a release.
//
// Environment: GH_TOKEN, GITHUB_REPOSITORY, GITHUB_SHA, GITHUB_API_URL (optional), GITHUB_OUTPUT and
// GITHUB_STEP_SUMMARY (optional). Writes release=true or release=false to GITHUB_OUTPUT.
//
// Tests: node --test eng/release-gate.test.mjs
import { appendFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

export const RELEASE_LABELS = ["cut-release", "security"];
const DEFAULT_API_URL = "https://api.github.com";
const PER_PAGE = 100;

/** The highest stable `vX.Y.Z` of git refs (`refs/tags/...`); prerelease tags belong to next and beta. */
export function lastReleaseTag(refs) {
  const versions = refs
    .map((ref) => /^refs\/tags\/(v(\d+)\.(\d+)\.(\d+))$/.exec(ref))
    .filter((match) => match !== null)
    .map((match) => ({ tag: match[1], parts: [Number(match[2]), Number(match[3]), Number(match[4])] }));
  versions.sort((a, b) => a.parts[0] - b.parts[0] || a.parts[1] - b.parts[1] || a.parts[2] - b.parts[2]);
  return versions.at(-1)?.tag;
}

function client({ apiUrl, token, fetch }) {
  const get = async (path) => {
    const response = await fetch(`${apiUrl}${path}`, {
      headers: { Accept: "application/vnd.github+json", Authorization: `Bearer ${token}` },
    });
    if (!response.ok) throw new Error(`GitHub answered ${response.status} to ${path}`);
    return response.json();
  };
  /** Every page of `path`; `items` picks the list out of a page. */
  const all = async (path, items = (page) => page) => {
    const result = [];
    for (let number = 1; ; number++) {
      const separator = path.includes("?") ? "&" : "?";
      const page = items(await get(`${path}${separator}per_page=${PER_PAGE}&page=${number}`));
      result.push(...page);
      if (page.length < PER_PAGE) return result;
    }
  };
  return { get, all };
}

/** The outcome for the commit `sha`: `{ release, reason, pullRequest? }`. */
export async function findRelease({ repo, sha, token, apiUrl = DEFAULT_API_URL, fetch = globalThis.fetch, labels = RELEASE_LABELS }) {
  const { get, all } = client({ apiUrl, token, fetch });
  const refs = await all(`/repos/${repo}/git/matching-refs/tags/v`);
  const tag = lastReleaseTag(refs.map((r) => r.ref));
  if (!tag) throw new Error("No release tag vX.Y.Z found: start the first release by hand (Actions > Release).");

  const commits = await all(`/repos/${repo}/compare/${tag}...${sha}`, (page) => page.commits);
  for (const commit of commits.reverse()) {
    for (const pull of await get(`/repos/${repo}/commits/${commit.sha}/pulls`)) {
      const label = pull.merged_at ? pull.labels.find((l) => labels.includes(l.name)) : undefined;
      if (label) return { release: true, pullRequest: pull.number, reason: `#${pull.number} has the label ${label.name}` };
    }
  }
  return { release: false, reason: `No pull request merged since ${tag} has the label ${labels.join(" or ")}` };
}

async function main() {
  const outcome = await findRelease({
    repo: process.env.GITHUB_REPOSITORY,
    sha: process.env.GITHUB_SHA,
    token: process.env.GH_TOKEN,
    apiUrl: process.env.GITHUB_API_URL ?? DEFAULT_API_URL,
  });
  const line = `${outcome.release ? "Releasing" : "Not releasing"}: ${outcome.reason}.`;
  console.log(line);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${line}\n`);
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `release=${outcome.release}\n`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) await main();
