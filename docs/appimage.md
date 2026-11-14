# Passenger as an AppImage

One file for Linux that runs as it is: no install, no root, nothing written outside Passenger's own state. It stands
beside the archive with its `install.sh` (`just package-linux`), which stays the package a release carries and the one
that updates itself.

## Making it

```sh
just package-appimage              # artifacts/package/Passenger-linux-x64.AppImage
just package-appimage linux-arm64  # artifacts/package/Passenger-linux-arm64.AppImage
```

Without `just`: `tools/package-appimage.sh [linux-x64|linux-arm64]`. It needs the .NET SDK `global.json` names,
`file`, squashfs-tools (`mksquashfs`, `unsquashfs`) and, the first time, `curl`. It does not need `appimagetool` or
FUSE.

What the script does:

1. **The runtime.** Downloads `runtime-x86_64` or `runtime-aarch64` of AppImage/type2-runtime at its release
   20251108 into `artifacts/package/appimage-runtime/` and holds it to the SHA-256 written in the script; a file that
   does not match is never used. `PASSENGER_APPIMAGE_RUNTIME=FILE` names a copy already on the machine (no network),
   held to the same SHA-256.
2. **The app.** The same self-contained, ReadyToRun Release publish the archive holds (`publish_app`).
3. **The AppDir.** `app/` (the publish), `AppRun` (the archive's launcher, which runs `app/Passenger` beside it), the
   desktop entry, the icon with its `.DirIcon`, and under `usr/share` the entry, icons and file types a desktop that
   integrates AppImages reads; `usr/bin/passenger-ide` links the launcher.
4. **The file.** `mksquashfs` (zstd, owned by root) makes the image, and the AppImage is the runtime followed by it.
5. **The check.** The image is read back from past the runtime's end: its layout, the desktop entry
   (`desktop-file-validate` where it is installed), every ELF file built for the right processor. On a Linux machine of
   that processor the AppImage then runs and draws its window off-screen, unpacked by the runtime itself
   (`APPIMAGE_EXTRACT_AND_RUN`), so the check passes in a container that has no FUSE.

A linux-x64 AppImage of 0.3.4 is 173 MB.

## Running it

```sh
chmod +x Passenger-linux-x64.AppImage
./Passenger-linux-x64.AppImage [FOLDER|FILE] [Passenger's flags]
```

The runtime is linked statically, so it needs no libfuse2; it mounts the image through the system's `fusermount3`
(or `fusermount`). Where there is neither (a container, some minimal systems),
`./Passenger-linux-x64.AppImage --appimage-extract-and-run` unpacks to a temporary folder and runs from it, and
`--appimage-extract` unpacks to `squashfs-root/` for good.

Checked on CachyOS (x64) on 2026-11-14: the AppImage started through FUSE and by unpacking, and Roslyn's server
answered for a C# project opened from it (an error underlined, semantic colours). Not checked: the arm64 AppImage,
another distribution, the debugger, the terminal and an agent's relay from inside the mount.

## How Passenger behaves in one

- **Nothing is written inside it.** The mount is read-only. Roslyn's server, which writes its caches beside its DLLs,
  already runs from a copy in the state's `cache/roslyn/` wherever its folder cannot be written
  (`RoslynServerFolder`).
- **It does not update itself.** The file cannot be replaced while it runs, so an update's notice says "This copy of
  Passenger is the AppImage …, which nothing can write while it runs, so it is updated by hand: a newer AppImage put in
  its place", links the release's page and downloads nothing (`LinuxUpdater.AppImageOf`, by the runtime's `APPIMAGE`
  and `APPDIR`). A Passenger started from a terminal inside an AppImage's Passenger is not taken for one: its folder is
  not the mounted one.
- **No menu entry.** Nothing is installed; a desktop that integrates AppImages (AppImageLauncher, Gear Lever) reads the
  entry and icon from the file.
- **The runtime's variables reach what Passenger starts.** `APPIMAGE`, `APPDIR`, `ARGV0` and `OWD` are in the
  environment of the terminal's shell and of every program run, as with any AppImage.

## What it carries

The runtime is MIT (Copyright (c) 2004-23 probonopd) and is linked statically with squashfuse (BSD-2-Clause), libfuse
3.15.0 with the runtime's own patch (LGPL-2.1), zstd, zlib, mimalloc and musl. `THIRD_PARTY_NOTICES.md` names it under
"The AppImage runtime". The libfuse part is the first LGPL code any Passenger package carries: it is unchanged from the
AppImage project's build and its source is theirs to get, but it is the owner's call whether a release ships it.

## Not done

- No release or CI job builds it (`release.yml`, `ci.yml`), and the site does not offer it: workflows run only by the
  owner's hand.
- No update information is embedded (`.upd_info`), so AppImageUpdate cannot update it, and it is not signed.
- No AppStream metadata (`usr/share/metainfo`), which a software centre would list it by.
