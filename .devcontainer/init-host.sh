#!/bin/sh
# Runs on the HOST before the dev container starts (initializeCommand). It only writes
# .devcontainer/.env (gitignored): where this checkout's git hooks and config live, so they can be
# mounted read-only. Nothing in the container can then plant a hook that runs on the host at your
# next commit. In a worktree .git is a file and has no hooks/ of its own, so the mounts go to
# harmless spare paths there.
set -eu
cd "$(dirname "$0")/.."
common=$(cd "$(git rev-parse --git-common-dir)" && pwd)
if [ -d .git ]; then
  hooks_dst=/workspace/.git/hooks
  config_dst=/workspace/.git/config
else
  hooks_dst=/run/ro-git/hooks
  config_dst=/run/ro-git/config
fi
mkdir -p "$common/hooks"
{
  echo "GIT_HOOKS_SRC=$common/hooks"
  echo "GIT_HOOKS_DST=$hooks_dst"
  echo "GIT_CONFIG_SRC=$common/config"
  echo "GIT_CONFIG_DST=$config_dst"
} > .devcontainer/.env
