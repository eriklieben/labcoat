// semantic-release plugin (a local one: release.config.mjs lists it by path) that makes the release commit through the
// GitHub API instead of `git commit` + `git push`. Used instead of @semantic-release/git when the release job has a
// token of the "labcoat-release" GitHub App (LABCOAT_RELEASE_COMMIT=api, see release.yml): GitHub signs commits that
// the GraphQL mutation createCommitOnBranch makes with an App installation token, so the release commit shows
// "Verified", authored by labcoat-release[bot]. Setup: docs/releasing.md, "Verified release commits (GitHub App)".
//
// prepare (after @semantic-release/changelog and `eng/release.mjs prepare` wrote CHANGELOG.md and
// src/Directory.Build.props):
//   1. createCommitOnBranch on the release branch with those files (base64), expectedHeadOid = the local HEAD (the
//      commit being released) and the message "chore(release): <version> [skip ci]" + the release notes. When the
//      branch has moved on, GitHub refuses it: the release stops here, before semantic-release creates any tag.
//   2. Fetch the new commit and move the local branch to it (`git reset`, the working tree is left alone: it already
//      has those exact files). semantic-release re-reads HEAD after each prepare step (nextRelease.gitHead), so it
//      tags v<version> on the API-made commit and pushes the tag and its git note with its own git credentials
//      (GIT_CREDENTIALS = the App token in release.yml).
//
// Environment: RELEASE_APP_TOKEN (the App installation token; never logged), GITHUB_REPOSITORY (owner/name, set by
// Actions), GITHUB_GRAPHQL_URL (optional, set by Actions; default https://api.github.com/graphql).
// Plugin options: assets (default CHANGELOG.md and src/Directory.Build.props), message (${nextRelease.version},
// ${nextRelease.gitTag}, ${nextRelease.notes} and ${branch.name} are filled in; default "chore(release): ${nextRelease.version} [skip ci]\n\n${nextRelease.notes}").
//
// Tests: node --test eng/release-commit.test.mjs
import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

export const DEFAULT_ASSETS = ["CHANGELOG.md", "src/Directory.Build.props"];
const DEFAULT_GRAPHQL_URL = "https://api.github.com/graphql";

const CREATE_COMMIT = `mutation ($input: CreateCommitOnBranchInput!) {
  createCommitOnBranch(input: $input) { commit { oid url } }
}`;
const BRANCH_HEAD = `query ($owner: String!, $name: String!, $ref: String!) {
  repository(owner: $owner, name: $name) { ref(qualifiedName: $ref) { target { oid } } }
}`;

export class ReleaseCommitError extends Error {
  constructor(message, details) {
    super(message);
    this.name = "ReleaseCommitError";
    // semantic-release prints `details` of an error with a `code` / `details` under the message.
    if (details) this.details = details;
    this.code = "ERELEASECOMMIT";
    this.semanticRelease = true;
  }
}

/** Replaces every occurrence of the given secrets (and their URL-encoded forms) in `text` with "[secure]". */
export function redact(text, secrets) {
  let out = String(text ?? "");
  // Longest first, so a URL that contains a token is replaced as a whole.
  for (const secret of [...secrets].sort((a, b) => String(b).length - String(a).length)) {
    if (!secret || secret.length < 5) continue;
    for (const form of new Set([secret, encodeURIComponent(secret)])) out = out.split(form).join("[secure]");
  }
  return out;
}

/** The release commit message (headline = first line, body = the rest). */
export function splitMessage(message) {
  const [headline, ...rest] = message.split("\n");
  return { headline: headline.trim(), body: rest.join("\n").trim() };
}

/** The `input` of createCommitOnBranch. `files` maps repository paths to their contents (Buffer or string). */
export function buildCommitInput({ repository, branch, expectedHeadOid, message, files }) {
  const { headline, body } = splitMessage(message);
  return {
    branch: { repositoryNameWithOwner: repository, branchName: branch },
    expectedHeadOid,
    message: body ? { headline, body } : { headline },
    fileChanges: {
      additions: Object.entries(files).map(([filePath, contents]) => ({
        path: filePath.split(path.sep).join("/"),
        contents: Buffer.from(contents).toString("base64"),
      })),
    },
  };
}

async function graphql({ token, url, query, variables, fetch }) {
  let response;
  try {
    response = await fetch(url, {
      method: "POST",
      headers: {
        Authorization: `bearer ${token}`,
        "Content-Type": "application/json",
        Accept: "application/vnd.github+json",
        "User-Agent": "labcoat-release",
      },
      body: JSON.stringify({ query, variables }),
    });
  } catch (error) {
    throw new ReleaseCommitError(`Could not reach the GitHub API (${url}): ${redact(error?.message, [token])}`);
  }
  const text = await response.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    json = undefined;
  }
  if (!response.ok) {
    const detail = redact(json?.message ?? text.slice(0, 300), [token]);
    const hint = response.status === 401
      ? " The App token is invalid or expired: check RELEASE_APP_ID and RELEASE_APP_PRIVATE_KEY."
      : response.status === 403 || response.status === 404
        ? " Check that the GitHub App is installed on this repository with Contents: read and write."
        : "";
    throw new ReleaseCommitError(`GitHub API request failed: HTTP ${response.status} ${detail}.${hint}`);
  }
  if (!json) throw new ReleaseCommitError(`GitHub API returned no JSON: ${redact(text.slice(0, 300), [token])}`);
  return json;
}

/**
 * Creates the release commit on `branch` with createCommitOnBranch. Returns { oid, url }.
 * Throws ReleaseCommitError (never containing the token) when GitHub refuses it, e.g. when the branch moved on.
 */
export async function createReleaseCommit({ token, repository, branch, expectedHeadOid, message, files, graphqlUrl = DEFAULT_GRAPHQL_URL, fetch = globalThis.fetch }) {
  if (!token) throw new ReleaseCommitError("RELEASE_APP_TOKEN is not set: the API release commit needs the GitHub App's installation token.");
  const input = buildCommitInput({ repository, branch, expectedHeadOid, message, files });
  const json = await graphql({ token, url: graphqlUrl, query: CREATE_COMMIT, variables: { input }, fetch });
  if (json.errors?.length) {
    const messages = json.errors.map((e) => redact(e.message, [token])).join("; ");
    const stale = json.errors.some((e) => e.type === "STALE_DATA" || /expected branch to point to|expectedheadoid|is not up to date/i.test(e.message ?? ""));
    if (stale) {
      throw new ReleaseCommitError(
        `${branch} has moved on from ${expectedHeadOid}: no release commit and no tag were created. The CI run of the newer commit starts the next release (it includes this one).`,
        `GitHub: ${messages}`,
      );
    }
    const forbidden = json.errors.some((e) => e.type === "FORBIDDEN" || /resource not accessible|permission/i.test(e.message ?? ""));
    throw new ReleaseCommitError(
      `GitHub refused the release commit on ${branch}: ${messages}`,
      forbidden
        ? "Check that the GitHub App is installed on this repository with Contents: read and write, and that a branch ruleset lists it as a bypass actor."
        : undefined,
    );
  }
  const commit = json.data?.createCommitOnBranch?.commit;
  if (!commit?.oid) throw new ReleaseCommitError(`GitHub returned no commit for the release commit on ${branch}.`);
  return { oid: commit.oid, url: commit.url };
}

/** The head of `branch` on GitHub (a read with the App token: proves the token works, also in a dry run). */
export async function getBranchHead({ token, repository, branch, graphqlUrl = DEFAULT_GRAPHQL_URL, fetch = globalThis.fetch }) {
  const [owner, name] = repository.split("/");
  const json = await graphql({ token, url: graphqlUrl, query: BRANCH_HEAD, variables: { owner, name, ref: `refs/heads/${branch}` }, fetch });
  if (json.errors?.length) throw new ReleaseCommitError(`GitHub API: ${json.errors.map((e) => redact(e.message, [token])).join("; ")}`);
  return json.data?.repository?.ref?.target?.oid ?? null;
}

function template(text, context) {
  // The same placeholders @semantic-release/git's message supports that this repository uses.
  // One pass, so text inside the notes is never substituted again.
  const values = {
    "nextRelease.version": context.nextRelease.version,
    "nextRelease.gitTag": context.nextRelease.gitTag ?? "",
    "nextRelease.notes": context.nextRelease.notes ?? "",
    "branch.name": context.branch.name,
  };
  return text.replace(/\$\{([\w.]+)\}/g, (match, key) => (key in values ? values[key] : match));
}

function settings(pluginConfig, { env }) {
  return {
    token: env.RELEASE_APP_TOKEN,
    repository: env.GITHUB_REPOSITORY,
    graphqlUrl: env.GITHUB_GRAPHQL_URL || DEFAULT_GRAPHQL_URL,
    assets: pluginConfig.assets ?? DEFAULT_ASSETS,
    message: pluginConfig.message ?? "chore(release): ${nextRelease.version} [skip ci]\n\n${nextRelease.notes}",
  };
}

const defaultGit = async (args, { cwd, env }) => (await execFileAsync("git", args, { cwd, env, maxBuffer: 16 * 1024 * 1024 })).stdout.trim();

export async function verifyConditions(pluginConfig, context, { fetch = globalThis.fetch } = {}) {
  const { token, repository, graphqlUrl } = settings(pluginConfig, context);
  if (!token) throw new ReleaseCommitError("LABCOAT_RELEASE_COMMIT=api but RELEASE_APP_TOKEN is not set (release.yml sets both from the GitHub App token step).");
  if (!/^[^/\s]+\/[^/\s]+$/.test(repository ?? "")) throw new ReleaseCommitError("GITHUB_REPOSITORY (owner/name) is not set.");
  const head = await getBranchHead({ token, repository, branch: context.branch.name, graphqlUrl, fetch });
  if (!head) throw new ReleaseCommitError(`The GitHub App token can't see the branch ${context.branch.name} of ${repository}.`);
  context.logger.log("Release commit: through the GitHub API as the GitHub App (%s %s is at %s)", repository, context.branch.name, head.slice(0, 12));
}

export async function prepare(pluginConfig, context, { fetch = globalThis.fetch, git = defaultGit } = {}) {
  const { cwd, env, logger, branch, options } = context;
  const { token, repository, graphqlUrl, assets, message } = settings(pluginConfig, context);
  const secrets = [token, options?.repositoryUrl, env.GIT_CREDENTIALS, env.GITHUB_TOKEN, env.GH_TOKEN].filter(Boolean);
  const run = async (args, what) => {
    try {
      return await git(args, { cwd, env });
    } catch (error) {
      throw new ReleaseCommitError(`git ${what} failed: ${redact(error?.stderr || error?.message, secrets).trim()}`);
    }
  };

  const expectedHeadOid = await run(["rev-parse", "HEAD"], "rev-parse HEAD");
  const files = {};
  for (const asset of assets) files[asset] = await readFile(path.resolve(cwd, asset));

  const commit = await createReleaseCommit({ token, repository, branch: branch.name, expectedHeadOid, message: template(message, context), files, graphqlUrl, fetch });
  logger.success("Created the release commit %s on %s through the GitHub API: %s", commit.oid, branch.name, commit.url ?? "");

  // Bring the commit into the checkout, then point the branch at it (index too; the working tree has the same files).
  await run(["fetch", "--no-tags", "--quiet", options.repositoryUrl, commit.oid], "fetch of the release commit");
  const parent = await run(["rev-parse", `${commit.oid}^`], "rev-parse of the release commit's parent");
  if (parent !== expectedHeadOid) {
    throw new ReleaseCommitError(`The release commit ${commit.oid} has parent ${parent}, expected ${expectedHeadOid}: not tagging it.`);
  }
  try {
    await git(["diff", "--quiet", commit.oid, "--", ...assets], { cwd, env });
  } catch {
    throw new ReleaseCommitError(`The release commit ${commit.oid} doesn't match the local ${assets.join(", ")}: not tagging it.`);
  }
  await run(["reset", "--quiet", commit.oid], "reset to the release commit");
  logger.log("Local %s is now at %s: semantic-release tags this commit", branch.name, commit.oid);
}
