#!/usr/bin/env bash
# A local copy of the repository beside GitHub's, made after a commit and push: `just backup` writes
#
#   Passenger.bundle              a git bundle of every branch and tag, checked with "git bundle verify"
#   Passenger.zip                 the versioned files of the commit checked out (git archive HEAD)
#   Passenger-linux-<arch>.AppImage   the app built from that commit for this machine (tools/package-appimage.sh)
#
# into PASSENGER_BACKUP_DIR, else ~/Dokumente. Only the newest of each is kept. Each is written under a name ending in
# .neu and renamed once it is made, so a run that fails leaves what was there before. The bundle and the zip are made
# first and stand whatever becomes of the AppImage, which takes minutes and needs the .NET SDK global.json names and
# squashfs-tools; --no-appimage leaves it out.
set -euo pipefail

target="${PASSENGER_BACKUP_DIR:-$HOME/Dokumente}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
name=Passenger
bundle="$target/$name.bundle"
zip="$target/$name.zip"
case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64) rid=linux-arm64 ;;
    *) rid= ;;
esac
image="$target/$name-$rid.AppImage"

[ -d "$target" ] || { echo "backup: there is no folder $target" >&2; exit 1; }
trap 'rm -f "$bundle.neu" "$zip.neu" "$image.neu"' EXIT
git -C "$repo" bundle create "$bundle.neu" --all 2>/dev/null
git -C "$repo" bundle verify "$bundle.neu" >/dev/null 2>&1 || { echo "backup: the bundle of $name does not verify" >&2; exit 1; }
git -C "$repo" archive --format=zip -o "$zip.neu" HEAD
mv -f "$bundle.neu" "$bundle"
mv -f "$zip.neu" "$zip"
echo "$name: $(git -C "$repo" rev-parse --short HEAD), bundle $(du -h "$bundle" | cut -f1), zip $(du -h "$zip" | cut -f1) in $target"

if [ "${1:-}" = --no-appimage ]; then
    exit 0
fi
[ -n "$rid" ] && [ "$(uname -s)" = Linux ] || { echo "backup: no AppImage is made on $(uname -s) $(uname -m)" >&2; exit 1; }
[ -z "$(git -C "$repo" status --porcelain)" ] || echo "backup: the AppImage is built from the working folder, which holds changes not committed" >&2
"$repo/tools/package-appimage.sh" "$rid" > "$target/.passenger-appimage.log" 2>&1 \
    || { echo "backup: the AppImage could not be made; its build's words are in $target/.passenger-appimage.log" >&2; exit 1; }
rm -f "$target/.passenger-appimage.log"
cp "$repo/artifacts/package/$name-$rid.AppImage" "$image.neu"
chmod 755 "$image.neu"
mv -f "$image.neu" "$image"
echo "$name: AppImage $(du -h "$image" | cut -f1) ($rid) in $target"
