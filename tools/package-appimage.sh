#!/bin/sh
# Passenger as an AppImage: `just package-appimage [linux-x64|linux-arm64]` makes
# artifacts/package/Passenger-linux-x64.AppImage (or -arm64), one file a person marks executable and runs, nothing
# installed. It is the AppImage project's runtime (AppImage/type2-runtime at the release pinned below, MIT, downloaded
# once and held to its SHA-256, or the file PASSENGER_APPIMAGE_RUNTIME names, held to it too) followed by a squashfs
# image of the AppDir: the Release publish in app/, the archive's launcher as AppRun, and the desktop entry, icons and
# file types of tools/linux. The image is then read back and checked: its layout, every ELF file built for its
# processor, and the AppImage running the app. Made wherever mksquashfs and unsquashfs are (squashfs-tools).
set -eu
. "$(dirname "$0")/release-common.sh"
rid=${1:-linux-x64}
runtime_release=20251108
case $rid in
    linux-x64) machine=x86-64; arch=x86_64; runtime_sum=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
    linux-arm64) machine=aarch64; arch=aarch64; runtime_sum=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
    *) fail "'$rid' is not a Linux system Passenger ships for: linux-x64 or linux-arm64" ;;
esac
for tool in file mksquashfs unsquashfs; do
    command -v "$tool" > /dev/null 2>&1 || fail "an AppImage needs $tool (file, and squashfs-tools' mksquashfs and unsquashfs)"
done
app=io.github.zaneris.passenger

root=$(cd "$(dirname "$0")/.." && pwd)
out="$root/artifacts/package"
work="$out/appimage-$rid"
publish="$work/publish"
appdir="$work/Passenger.AppDir"
check="$work/check"
image="$out/Passenger-linux-${rid#linux-}.AppImage"

# The SHA-256 of a file, by whichever of the two tools the machine has.
sum_of() {
    if command -v sha256sum > /dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    else
        shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

# The runtime, kept between runs beside the packages; one that does not match its SHA-256 is never used.
runtime=${PASSENGER_APPIMAGE_RUNTIME:-}
if [ -z "$runtime" ]; then
    runtime="$out/appimage-runtime/$runtime_release/runtime-$arch"
    if [ ! -f "$runtime" ] || [ "$(sum_of "$runtime")" != "$runtime_sum" ]; then
        command -v curl > /dev/null 2>&1 || fail "downloading the AppImage runtime needs curl; or name its file in PASSENGER_APPIMAGE_RUNTIME"
        mkdir -p "$(dirname "$runtime")"
        curl -fsSL -o "$runtime" "https://github.com/AppImage/type2-runtime/releases/download/$runtime_release/runtime-$arch" \
            || fail "the AppImage runtime could not be downloaded; or name its file in PASSENGER_APPIMAGE_RUNTIME"
    fi
fi
[ -f "$runtime" ] || fail "there is no AppImage runtime at $runtime"
found=$(sum_of "$runtime")
[ "$found" = "$runtime_sum" ] || fail "$runtime is not type2-runtime $runtime_release's runtime-$arch: its SHA-256 is $found, not $runtime_sum"

version=$(release_version "$root")

rm -rf "$work" "$image"
mkdir -p "$work"
publish_app "$root" "$rid" "$version" -o "$publish" -nologo -v quiet

# The AppDir as the launcher and a desktop read it; the entry without the TryExec of a command only install.sh installs.
linux="$root/tools/linux"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/mime/packages" \
    "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/icons/hicolor/scalable/apps"
cp -R "$publish" "$appdir/app"
cp "$linux/passenger" "$appdir/AppRun"
grep -v '^TryExec=' "$linux/$app.desktop" > "$appdir/$app.desktop"
cp "$appdir/$app.desktop" "$appdir/usr/share/applications/$app.desktop"
cp "$linux/$app.xml" "$appdir/usr/share/mime/packages/$app.xml"
cp "$root/src/Passenger.App/Assets/passenger.png" "$appdir/$app.png"
cp "$root/src/Passenger.App/Assets/passenger.png" "$appdir/usr/share/icons/hicolor/256x256/apps/$app.png"
cp "$root/src/Passenger.App/Assets/passenger.svg" "$appdir/usr/share/icons/hicolor/scalable/apps/$app.svg"
ln -s "$app.png" "$appdir/.DirIcon"
ln -s ../../AppRun "$appdir/usr/bin/passenger-ide"
chmod 755 "$appdir/AppRun"
chmod -R u+rwX,go+rX,go-w "$appdir"

# Root's and readable by all; the runtime mounts the image that follows it in the file.
mksquashfs "$appdir" "$work/image.squashfs" -noappend -all-root -no-xattrs -no-progress -comp zstd > "$work/mksquashfs.log" \
    || fail "mksquashfs could not make the image: $(tail -n 3 "$work/mksquashfs.log")"
cat "$runtime" "$work/image.squashfs" > "$image"
chmod 755 "$image"
rm "$work/image.squashfs"

# Read back from past the runtime's end, as it mounts it; an ELF file for another processor fails only when loaded.
offset=$(wc -c < "$runtime" | tr -d ' ')
unsquashfs -q -n -o "$offset" -d "$check" "$image" > /dev/null || fail "the AppImage's image cannot be read back"
[ -x "$check/AppRun" ] || fail "the AppImage has no executable AppRun"
for part in app/Passenger app/passenger-helper app/createdump; do
    [ -x "$check/$part" ] || fail "the AppImage has no executable $part"
done
for part in "$app.desktop" "$app.png" "usr/share/applications/$app.desktop" "usr/share/mime/packages/$app.xml" \
    "usr/share/icons/hicolor/256x256/apps/$app.png" "usr/share/icons/hicolor/scalable/apps/$app.svg" \
    app/Passenger.dll app/libSkiaSharp.so app/libHarfBuzzSharp.so app/libporta_pty.so app/LICENSE.txt app/THIRD-PARTY-NOTICES.txt \
    app/roslyn/Microsoft.CodeAnalysis.LanguageServer.dll app/roslyn-extension/Passenger.Roslyn.Extension.dll \
    app/debugger/Passenger.DebugAdapter.dll app/debugger/libdbgshim.so app/passenger-helper.dll \
    app/visualbasic/Passenger.VisualBasic.dll app/visualbasic/Microsoft.CodeAnalysis.VisualBasic.Features.dll; do
    [ -f "$check/$part" ] || fail "the AppImage has no $part"
done
[ -L "$check/.DirIcon" ] && [ -f "$check/.DirIcon" ] || fail "the AppImage's .DirIcon names no icon"
[ -L "$check/usr/bin/passenger-ide" ] && [ -x "$check/usr/bin/passenger-ide" ] || fail "the AppImage's usr/bin/passenger-ide names no launcher"
grep -q '^StartupWMClass=Passenger$' "$check/$app.desktop" || fail "the desktop entry does not match the window's class, Passenger"
! grep -q '^TryExec=' "$check/$app.desktop" || fail "the desktop entry asks for a command an AppImage puts on no PATH"
if command -v desktop-file-validate > /dev/null 2>&1; then
    desktop-file-validate "$check/$app.desktop" || fail "the desktop entry is not a valid one"
fi
find "$check" -type f -exec file -F '|' {} + | grep '| *ELF' > "$work/elf" || true
[ -s "$work/elf" ] || fail "the AppImage has no ELF file"
wrong=$(grep -v "$machine" "$work/elf" | cut -d'|' -f1 | sed "s:^$check/::" || true)
[ -z "$wrong" ] || fail "built for another processor than $rid's: $(echo "$wrong" | head -n 5 | tr '\n' ' ')"
file "$image" | grep -q "$machine" || fail "the AppImage's runtime is not $rid's"
# On a Linux machine of its processor it runs the app, unpacked by the runtime itself: a container has no FUSE.
if [ "$(uname -s)" = Linux ] && [ "$(uname -m)" = "$arch" ]; then
    APPIMAGE_EXTRACT_AND_RUN=1 PASSENGER_SETTINGS="$work/state" "$image" --screenshot "$work/welcome.png" --size 1000x700 > /dev/null \
        || fail "the AppImage could not run the app"
    [ -s "$work/welcome.png" ] || fail "the app ran but drew no window"
fi
rm -rf "$check"

size=$(du -h "$image" | cut -f1 | tr -d ' ')
echo "package: $image ($size, Passenger $version for $rid)"
