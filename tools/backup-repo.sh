#!/usr/bin/env bash
# A local copy of the repository beside GitHub's, made after a commit and push: `just backup` writes
#
#   Passenger.bundle   a git bundle of every branch and tag, checked with "git bundle verify"
#   Passenger.zip      the versioned files of the commit checked out (git archive HEAD)
#
# into PASSENGER_BACKUP_DIR, else ~/Dokumente. Only the newest pair is kept. Each is written under a name ending in
# .neu and renamed once both are made, so a run that fails leaves the pair before it as it was.
set -euo pipefail

target="${PASSENGER_BACKUP_DIR:-$HOME/Dokumente}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
name=Passenger
bundle="$target/$name.bundle"
zip="$target/$name.zip"

[ -d "$target" ] || { echo "backup: there is no folder $target" >&2; exit 1; }
trap 'rm -f "$bundle.neu" "$zip.neu"' EXIT
git -C "$repo" bundle create "$bundle.neu" --all 2>/dev/null
git -C "$repo" bundle verify "$bundle.neu" >/dev/null 2>&1 || { echo "backup: the bundle of $name does not verify" >&2; exit 1; }
git -C "$repo" archive --format=zip -o "$zip.neu" HEAD
mv -f "$bundle.neu" "$bundle"
mv -f "$zip.neu" "$zip"
echo "$name: $(git -C "$repo" rev-parse --short HEAD), bundle $(du -h "$bundle" | cut -f1), zip $(du -h "$zip" | cut -f1) in $target"
