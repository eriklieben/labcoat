// Tests of eng/release-commit.mjs with a mocked fetch and git (no network, no repository changes):
//   node --test eng/release-commit.test.mjs
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { format } from "node:util";
import path from "node:path";
import { after, describe, it } from "node:test";
import { buildCommitInput, createReleaseCommit, prepare, redact, ReleaseCommitError, verifyConditions } from "./release-commit.mjs";

const TOKEN = "ghs_FAKEtoken1234567890abcdefABCDEF";
const HEAD = "1111111111111111111111111111111111111111";
const NEW = "2222222222222222222222222222222222222222";
const AUTH_URL = `https://x-access-token:${TOKEN}@github.com/eriklieben/labcoat.git`;

const cwd = mkdtempSync(path.join(tmpdir(), "release-commit-"));
mkdirSync(path.join(cwd, "src"));
writeFileSync(path.join(cwd, "CHANGELOG.md"), "# Changelog\n\n## 1.2.3 ünïcode ✓\n");
writeFileSync(path.join(cwd, "src", "Directory.Build.props"), "<Project><VersionPrefix>1.2.3</VersionPrefix></Project>\n");
after(() => rmSync(cwd, { recursive: true, force: true }));

function mockFetch(responder) {
  const calls = [];
  const fetch = async (url, init) => {
    calls.push({ url, init, body: JSON.parse(init.body) });
    const { status = 200, json, text } = responder(calls.at(-1));
    return { ok: status >= 200 && status < 300, status, text: async () => (text ?? JSON.stringify(json)) };
  };
  return { fetch, calls };
}

function mockGit({ parent = HEAD, diffFails = false, fetchError } = {}) {
  const calls = [];
  const git = async (args) => {
    calls.push(args);
    if (args[0] === "rev-parse" && args[1] === "HEAD") return HEAD;
    if (args[0] === "rev-parse") return parent;
    if (args[0] === "fetch" && fetchError) throw Object.assign(new Error("Command failed"), { stderr: fetchError });
    if (args[0] === "diff" && diffFails) throw new Error("exit 1");
    return "";
  };
  return { git, calls };
}

function context(overrides = {}) {
  const logs = [];
  const log = (...args) => logs.push(format(...args));
  return {
    logs,
    cwd,
    env: { RELEASE_APP_TOKEN: TOKEN, GITHUB_REPOSITORY: "eriklieben/labcoat", GIT_CREDENTIALS: `x-access-token:${TOKEN}` },
    logger: { log, success: log, warn: log, error: log },
    branch: { name: "main" },
    options: { repositoryUrl: AUTH_URL },
    nextRelease: { version: "1.2.3", gitTag: "v1.2.3", notes: "## 1.2.3\n\n### Bug Fixes\n\n* a fix ${branch.name}" },
    ...overrides,
  };
}

const config = { message: "chore(release): ${nextRelease.version} [skip ci]\n\n${nextRelease.notes}" };
const created = () => ({ json: { data: { createCommitOnBranch: { commit: { oid: NEW, url: `https://github.com/eriklieben/labcoat/commit/${NEW}` } } } } });
const assertNoToken = (...texts) => {
  for (const text of texts) {
    assert.ok(!String(text).includes(TOKEN), `token leaked in: ${text}`);
    assert.ok(!String(text).includes(encodeURIComponent(TOKEN)), `encoded token leaked in: ${text}`);
  }
};

describe("buildCommitInput", () => {
  it("base64-encodes the files and splits the message", () => {
    const input = buildCommitInput({
      repository: "o/r", branch: "main", expectedHeadOid: HEAD,
      message: "chore(release): 1.0.0 [skip ci]\n\nnotes line 1\nline 2",
      files: { "CHANGELOG.md": Buffer.from("é ✓"), "src/x.props": "<a/>" },
    });
    assert.deepEqual(input, {
      branch: { repositoryNameWithOwner: "o/r", branchName: "main" },
      expectedHeadOid: HEAD,
      message: { headline: "chore(release): 1.0.0 [skip ci]", body: "notes line 1\nline 2" },
      fileChanges: {
        additions: [
          { path: "CHANGELOG.md", contents: Buffer.from("é ✓").toString("base64") },
          { path: "src/x.props", contents: Buffer.from("<a/>").toString("base64") },
        ],
      },
    });
  });
});

describe("prepare", () => {
  it("sends createCommitOnBranch with the files, HEAD and branch, then moves the checkout to the new commit", async () => {
    const { fetch, calls } = mockFetch(created);
    const { git, calls: gitCalls } = mockGit();
    const ctx = context();
    await prepare(config, ctx, { fetch, git });

    assert.equal(calls.length, 1);
    const [{ url, init, body }] = calls;
    assert.equal(url, "https://api.github.com/graphql");
    assert.equal(init.method, "POST");
    assert.equal(init.headers.Authorization, `bearer ${TOKEN}`);
    assert.match(body.query, /createCommitOnBranch\(input: \$input\)/);
    const { input } = body.variables;
    assert.deepEqual(input.branch, { repositoryNameWithOwner: "eriklieben/labcoat", branchName: "main" });
    assert.equal(input.expectedHeadOid, HEAD);
    assert.equal(input.message.headline, "chore(release): 1.2.3 [skip ci]");
    // The notes go in verbatim: placeholders inside them are not filled in.
    assert.equal(input.message.body, "## 1.2.3\n\n### Bug Fixes\n\n* a fix ${branch.name}");
    const additions = Object.fromEntries(input.fileChanges.additions.map((a) => [a.path, Buffer.from(a.contents, "base64").toString("utf8")]));
    assert.deepEqual(additions, {
      "CHANGELOG.md": "# Changelog\n\n## 1.2.3 ünïcode ✓\n",
      "src/Directory.Build.props": "<Project><VersionPrefix>1.2.3</VersionPrefix></Project>\n",
    });
    assert.equal(input.fileChanges.deletions, undefined);

    assert.deepEqual(gitCalls, [
      ["rev-parse", "HEAD"],
      ["fetch", "--no-tags", "--quiet", AUTH_URL, NEW],
      ["rev-parse", `${NEW}^`],
      ["diff", "--quiet", NEW, "--", "CHANGELOG.md", "src/Directory.Build.props"],
      ["reset", "--quiet", NEW],
    ]);
    assertNoToken(...ctx.logs);
  });

  it("uses GITHUB_GRAPHQL_URL when set (GitHub Enterprise, tests)", async () => {
    const { fetch, calls } = mockFetch(created);
    const ctx = context();
    ctx.env.GITHUB_GRAPHQL_URL = "http://127.0.0.1:1/graphql";
    await prepare(config, ctx, { fetch, git: mockGit().git });
    assert.equal(calls[0].url, "http://127.0.0.1:1/graphql");
  });

  it("stops with a clear error and no fetch/reset when the branch moved on (no tag follows)", async () => {
    const { fetch } = mockFetch(() => ({
      json: { data: { createCommitOnBranch: null }, errors: [{ type: "STALE_DATA", message: `Expected branch to point to "${HEAD}" but it did not. Pull and try again.` }] },
    }));
    const { git, calls: gitCalls } = mockGit();
    const ctx = context();
    const error = await prepare(config, ctx, { fetch, git }).then(() => null, (e) => e);
    assert.ok(error instanceof ReleaseCommitError);
    assert.match(error.message, /main has moved on from 1111111111111111111111111111111111111111: no release commit and no tag were created/);
    assert.deepEqual(gitCalls, [["rev-parse", "HEAD"]]);
    assertNoToken(error.message, error.details, ...ctx.logs);
  });

  it("explains a permission error", async () => {
    const { fetch } = mockFetch(() => ({ json: { errors: [{ type: "FORBIDDEN", message: "Resource not accessible by integration" }] } }));
    const error = await prepare(config, context(), { fetch, git: mockGit().git }).then(() => null, (e) => e);
    assert.match(error.message, /GitHub refused the release commit on main: Resource not accessible by integration/);
    assert.match(error.details, /Contents: read and write/);
  });

  it("never puts the token in errors, also when GitHub or git echo it back", async () => {
    const { fetch } = mockFetch(() => ({ status: 401, text: `{"message":"Bad credentials ${TOKEN}"}` }));
    const ctx = context();
    const e1 = await prepare(config, ctx, { fetch, git: mockGit().git }).then(() => null, (e) => e);
    assert.match(e1.message, /HTTP 401 Bad credentials \[secure\]/);
    assertNoToken(e1.message, e1.details);

    const e2 = await prepare(config, ctx, {
      fetch: mockFetch(created).fetch,
      git: mockGit({ fetchError: `fatal: unable to access '${AUTH_URL}': The requested URL returned error: 403` }).git,
    }).then(() => null, (e) => e);
    assert.match(e2.message, /git fetch of the release commit failed: fatal: unable to access '\[secure\]'/);
    assertNoToken(e2.message, ...ctx.logs);

    const e3 = await createReleaseCommit({
      token: TOKEN, repository: "o/r", branch: "main", expectedHeadOid: HEAD, message: "m", files: {},
      fetch: async () => { throw new Error(`connect failed for ${TOKEN}`); },
    }).then(() => null, (e) => e);
    assertNoToken(e3.message);
  });

  it("refuses to tag a commit whose parent isn't the released HEAD or whose files differ", async () => {
    const wrongParent = await prepare(config, context(), { fetch: mockFetch(created).fetch, git: mockGit({ parent: "3".repeat(40) }).git }).then(() => null, (e) => e);
    assert.match(wrongParent.message, /has parent 3{40}, expected 1{40}: not tagging it/);

    const { git, calls } = mockGit({ diffFails: true });
    const differs = await prepare(config, context(), { fetch: mockFetch(created).fetch, git }).then(() => null, (e) => e);
    assert.match(differs.message, /doesn't match the local CHANGELOG.md, src\/Directory.Build.props: not tagging it/);
    assert.ok(!calls.some((c) => c[0] === "reset"));
  });

  it("requires the token", async () => {
    const ctx = context();
    delete ctx.env.RELEASE_APP_TOKEN;
    const { fetch, calls } = mockFetch(created);
    await assert.rejects(prepare(config, ctx, { fetch, git: mockGit().git }), /RELEASE_APP_TOKEN is not set/);
    assert.equal(calls.length, 0);
  });
});

describe("verifyConditions", () => {
  it("reads the branch head with the token", async () => {
    const { fetch, calls } = mockFetch(() => ({ json: { data: { repository: { ref: { target: { oid: HEAD } } } } } }));
    const ctx = context();
    await verifyConditions(config, ctx, { fetch });
    assert.deepEqual(calls[0].body.variables, { owner: "eriklieben", name: "labcoat", ref: "refs/heads/main" });
    assert.match(ctx.logs.join("\n"), /eriklieben\/labcoat main is at 111111111111/);
    assertNoToken(...ctx.logs);
  });

  it("fails without a token, a repository or a visible branch", async () => {
    const noToken = context();
    delete noToken.env.RELEASE_APP_TOKEN;
    await assert.rejects(verifyConditions(config, noToken, { fetch: mockFetch(created).fetch }), /RELEASE_APP_TOKEN is not set/);
    const noRepo = context();
    delete noRepo.env.GITHUB_REPOSITORY;
    await assert.rejects(verifyConditions(config, noRepo, { fetch: mockFetch(created).fetch }), /GITHUB_REPOSITORY/);
    const { fetch } = mockFetch(() => ({ json: { data: { repository: null } } }));
    await assert.rejects(verifyConditions(config, context(), { fetch }), /can't see the branch main/);
  });
});

describe("redact", () => {
  it("replaces plain and URL-encoded secrets", () => {
    assert.equal(redact("a x:y/z b x%3Ay%2Fz", ["x:y/z"]), "a [secure] b [secure]");
    assert.equal(redact("short abc", ["abc"]), "short abc");
  });
});
