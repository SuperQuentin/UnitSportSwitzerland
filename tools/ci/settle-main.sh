#!/usr/bin/env bash
# Pick the commit a CI release should build, coalescing a burst of merges into one release.
# Waits until origin/main has sat still for QUIET_MINUTES, restarting that clock on every new merge,
# then checks that tip out detached. Exits early ("skip") when the tip is already released, which is
# what makes the runs queued behind the first one cheap. Used by .github/workflows/release.yml.
# Env: QUIET_MINUTES (3), POLL_SECONDS (30), CAP_SECONDS (3000: release anyway if merges never stop),
#      QUIET_SECONDS (overrides QUIET_MINUTES; the tests use it to exercise the restart in seconds).
# Writes skip=0|1 and tip=<sha> to $GITHUB_OUTPUT when set, and prints the same.
set -euo pipefail
cd "$(dirname "$0")/../.."
QUIET_MINUTES=${QUIET_MINUTES:-3} POLL_SECONDS=${POLL_SECONDS:-30} CAP_SECONDS=${CAP_SECONDS:-3000}

out() { # key=value
  echo "$1"
  [ -n "${GITHUB_OUTPUT:-}" ] && echo "$1" >> "$GITHUB_OUTPUT"
  return 0
}
released_as() { git tag --points-at "$1" --list 'v[0-9]*' | head -1; }

quiet=${QUIET_SECONDS:-$(( QUIET_MINUTES * 60 ))}
tip=$(git rev-parse origin/main)
start=$SECONDS stable=$SECONDS
while :; do
  tag=$(released_as "$tip")
  if [ -n "$tag" ]; then
    echo "$tip is already released as $tag; nothing to do."
    out "skip=1"; exit 0
  fi
  if [ $(( SECONDS - stable )) -ge $quiet ]; then
    echo "main has been at $tip for ${quiet}s; releasing it."; break
  fi
  if [ $(( SECONDS - start )) -ge "$CAP_SECONDS" ]; then
    echo "Merges kept coming for $(( CAP_SECONDS / 60 ))m; releasing $tip anyway."; break
  fi
  sleep "$POLL_SECONDS"
  git fetch -q origin main --tags
  now=$(git rev-parse origin/main)
  if [ "$now" != "$tip" ]; then
    echo "main moved to $now; restarting the ${quiet}s quiet period."
    tip=$now; stable=$SECONDS
  fi
done
git checkout -q --detach "$tip"
out "tip=$tip"; out "skip=0"
