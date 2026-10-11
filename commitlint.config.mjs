// Commit message rules: the Angular format, `type(scope): subject`, checked on pull request titles (the squash-merge
// commit message) by .github/workflows/commit-messages.yml and, after `npm install` in the repository root, by a local
// commit-msg hook. Details and examples: CONTRIBUTING.md, "Commit messages and releases".
//
//   echo "feat(ui): add a duration filter" | npx commitlint
//
// config-conventional = the Angular types (build, ci, docs, feat, fix, perf, refactor, style, test) plus chore and
// revert, and also accepts the `!` breaking-change marker (`feat(host)!: ...`), which release.config.mjs understands.
export default {
  extends: ["@commitlint/config-conventional"],
  rules: {
    // Suggested scopes: a warning, not an error, and the scope stays optional. Keep in sync with CONTRIBUTING.md.
    "scope-enum": [
      1,
      "always",
      ["ui", "design-system", "host", "aspire", "testing", "tpext", "cli", "docs", "ci", "deps", "deps-dev", "samples", "release"],
    ],
    // Bodies and footers hold URLs, stack traces and generated release notes (Renovate, semantic-release): no
    // line length limit there. The header (the line that ends up in the changelog) stays at 100 characters.
    "body-max-line-length": [0],
    "footer-max-line-length": [0],
  },
};
