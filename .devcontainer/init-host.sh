#!/bin/sh
# Runs on the HOST before the dev container starts (initializeCommand). It only writes
# .devcontainer/.env (gitignored): where this checkout's git data lives, so the container can run git
# and still cannot plant a hook that runs on the host at your next commit (hooks/ and config are
# mounted read-only).
# In a worktree .git is a file pointing at <main>/.git/worktrees/<name> by absolute path, so the
# main repo's git dir is mounted at that same path inside the container and git works unmodified.
# The worktree's own config.worktree is read-only too (it can carry core.hooksPath).
# DC_PROJECT is the compose project name (compose.yaml: name: ${DC_PROJECT:-<repo>}); a worktree gets
# the worktree's folder name appended, so it has its own containers and volumes next to the main clone.
set -eu
cd "$(dirname "$0")/.."
common=$(cd "$(git rev-parse --git-common-dir)" && pwd)
mkdir -p "$common/hooks"
if [ -d .git ]; then
  git_dst=/workspace/.git
  wt_src=$common/config
  wt_dst=/run/ro-git/config.worktree
else
  git_dst=$common
  gitdir=$(cd "$(git rev-parse --git-dir)" && pwd)
  [ -e "$gitdir/config.worktree" ] || : > "$gitdir/config.worktree"
  wt_src=$gitdir/config.worktree
  wt_dst=$gitdir/config.worktree
fi
project=$(sed -n 's/^name:[[:space:]]*\${DC_PROJECT:-\(.*\)}[[:space:]]*$/\1/p' .devcontainer/compose.yaml | head -1)
if [ -n "$project" ] && [ ! -d .git ]; then
  wt=$(basename "$PWD" | tr 'A-Z' 'a-z' | sed 's/[^a-z0-9]\{1,\}/-/g; s/^-//; s/-$//')
  project=$project-$wt
fi
{
  [ -z "$project" ] || echo "DC_PROJECT=$project"
  echo "GIT_COMMON_SRC=$common"
  echo "GIT_COMMON_DST=$git_dst"
  echo "GIT_HOOKS_SRC=$common/hooks"
  echo "GIT_HOOKS_DST=$git_dst/hooks"
  echo "GIT_CONFIG_SRC=$common/config"
  echo "GIT_CONFIG_DST=$git_dst/config"
  echo "GIT_WT_SRC=$wt_src"
  echo "GIT_WT_DST=$wt_dst"
} > .devcontainer/.env
