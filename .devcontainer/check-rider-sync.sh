#!/bin/bash
# The Rider devcontainer.json must stay the default one apart from its name and its compose files (Rider adds the
# /workspace mount itself, see compose.workspace.yaml). Fails when the two files drift.
set -eu
cd "$(dirname "$0")"

normalize() {
  sed -e 's|^  "name": .*|  "name": "NAME",|' \
      -e 's|^  "dockerComposeFile": .*|  "dockerComposeFile": COMPOSE,|' "$1"
}

if ! diff -u --label devcontainer.json --label rider/devcontainer.json \
    <(normalize devcontainer.json) <(normalize rider/devcontainer.json); then
  echo "rider/devcontainer.json differs from devcontainer.json beyond name and dockerComposeFile." >&2
  exit 1
fi
