## What and why

<!-- What does this change, and why? Link the issue it fixes (e.g. "Fixes #123"). For anything bigger than a small fix,
link the issue or discussion where it was agreed on: pull requests without that may be closed
(see CONTRIBUTING.md, "What goes into Labcoat"). -->

## How it was tested

<!-- Commands you ran and what you checked by hand (which sample, which theme, which OS). -->

## Checklist

- [ ] `dotnet build Labcoat.slnx -warnaserror` and `dotnet test --solution Labcoat.slnx` pass
- [ ] UI changes: `pnpm run typecheck`, `pnpm run build` and the relevant `pnpm run check:*` scripts pass (in `ui/`)
- [ ] UI changes: checked in light and dark mode; screenshots below
- [ ] Docs updated (README, `docs/`, package READMEs) when behavior or options changed
- [ ] The PR title follows the [commit message format](../CONTRIBUTING.md#commit-messages-and-releases) (`feat(ui): ...`, `fix(host): ...`; it becomes the changelog entry)

## Screenshots

<!-- For UI changes: before / after. -->
