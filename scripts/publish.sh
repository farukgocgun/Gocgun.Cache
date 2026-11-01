#!/usr/bin/env bash
# Publishes Gocgun.Cache to nuget.org: dispatches the manual `release.yml` workflow of every
# dependency first (in dependency order), then of this repository, waiting for each run.
# Requires the GitHub CLI (`gh auth login`). No secrets are read or stored.
set -euo pipefail
OWNER="farukgocgun"
# Dependency order: bases first, this package last.
REPOS=(Gocgun.Cache )
command -v gh >/dev/null || { echo "gh (GitHub CLI) is required." >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Run 'gh auth login' first." >&2; exit 1; }
for repo in "${REPOS[@]}"; do
  if [ "$repo" = "Gocgun.Diagnostics.Shared" ]; then
    echo "Skipping $repo: it is not packed and has no release workflow."
    continue
  fi
  echo "==> Dispatching release.yml in $OWNER/$repo (publish=true)"
  before=$(gh run list -R "$OWNER/$repo" --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId // 0')
  gh workflow run release.yml -R "$OWNER/$repo" -f publish=true
  run_id=""
  for _ in $(seq 1 30); do
    sleep 2
    run_id=$(gh run list -R "$OWNER/$repo" --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId // 0')
    [ "$run_id" != "$before" ] && [ "$run_id" != "0" ] && break
    run_id=""
  done
  [ -n "$run_id" ] || { echo "Could not find the new run of $repo." >&2; exit 1; }
  gh run watch "$run_id" -R "$OWNER/$repo" --exit-status || { echo "Release of $repo failed; stopping." >&2; exit 1; }
done
echo "Done."
