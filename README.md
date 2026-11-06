# MLV Drive

**Edit Magic Lantern raw video in DaVinci Resolve straight from your card or drive, with nothing to convert.**

MLV Drive adds a virtual drive to your computer. Every `.MLV` clip on your connected drives
appears there as a CinemaDNG folder with its audio. Drag a clip into Resolve and you get the
full Camera Raw controls: white balance, ISO, highlight recovery, decode quality and colour space.

- **No waiting.** Frames are made on the fly as Resolve reads them, so nothing is copied.
- **No risk to your footage.** The drive is read-only and your clips are never written to.
- **No wasted disk space.** A 100 GB clip stays 100 GB, not 100 GB plus a DNG copy.
- **Projects relink.** The same drive gets the same paths every time you plug it in.

## Quick start

1. Download the file for your computer from the [latest release](https://github.com/charlie-schomp/MLVDrive/releases/latest).
2. Install it using the steps for [Windows](#windows), [macOS](#macos) or [Linux](#linux) below.
3. Plug in your card or drive. Your clips appear on the virtual drive within a few seconds.
4. In Resolve, open the **Media** page, browse to the virtual drive and drag clip folders into the media pool.

Every clip becomes a folder like this:

```
M:\SSD 2TB\DCIM\100CANON\M08-1651\
    M08-1651_000000.dng
    M08-1651_000001.dng
    ...
    M08-1651.wav
```

Each drive also gets a `_metadata.csv` with every clip's scene, take, ISO, shutter and lens,
in the column names Resolve uses for **Import Metadata**.

## Which download?

| Your computer | Download | Needs | How well tested |
|---|---|---|---|
| **Windows 10/11** (most PCs) | `MLV-Drive-<version>-windows-x64.zip` | WinFsp (the installer offers it) | Used day to day with Resolve |
| **Windows on ARM** | `MLV-Drive-<version>-windows-arm64.zip` | WinFsp | Built, never run. Please report back |
| **Mac** (Apple Silicon or Intel) | `MLV-Drive-<version>-macos.zip` | FUSE-T or macFUSE | Mount test passes with FUSE-T; not yet tried with Resolve or macFUSE |
| **Linux x64** | `MLV-Drive-<version>-linux-x64.tar.gz` | libfuse 3 or 2 | Mount test passes on both; not yet tried with Resolve |
| **Linux ARM** | `MLV-Drive-<version>-linux-arm64.tar.gz` | libfuse 3 or 2 | Mount test passes on libfuse 3; not yet tried with Resolve |

The mount test mounts a test clip and checks every frame and the audio byte for byte. You don't
need to install .NET on any of them. Each download also includes a command-line tool
that needs no drive at all (see [Command line](#command-line)).

## Windows

Unzip the download and double-click **INSTALL.cmd**.

- It installs for your account only, in `%LOCALAPPDATA%\Programs\MLV Drive`, and starts with Windows.
- It needs **WinFsp**, the free virtual-drive driver from [winfsp.dev](https://winfsp.dev). If it's
  missing, the installer adds a pinned release using winget, or from WinFsp's GitHub release after
  checking its checksum and signature. This is the only step where Windows asks for permission.
- `MLVDrive.exe` isn't code-signed yet, so SmartScreen may warn you the first time. Choose
  **More info > Run anyway**. `SHA256SUMS.txt` lists every file's checksum.

MLV Drive then runs in the system tray and shows your clips on **M:**. Right-click the tray icon for:

- **Open drive** and **Look for clips again**
- **Frame rate in DNGs**, for clips whose recorded rate is wrong
- **Drive letter**
- **Also show a folder...**, for folders on the Windows drive, which isn't scanned by default
- **Start with Windows**, **Notifications** and **Open log**

To remove it, run **UNINSTALL.cmd**. WinFsp stays installed, and you can remove it in Settings > Apps.

## macOS

1. Install [FUSE-T](https://www.fuse-t.org), which is free and needs no kernel extension or security changes:

   ```bash
   brew install macos-fuse-t/homebrew-cask/fuse-t
   ```

   If you already have macFUSE, that works too.
2. Unzip the download, open Terminal in that folder and run:

   ```bash
   bash "MLV Drive.command"
   ```

Your clips appear in **~/MLV Drive**. Add that folder under **Media Storage** in Resolve, and drag
clips in from Resolve's browser rather than Finder. Press Ctrl+C to stop.

The launcher signs the app for your Mac and clears the download warning, touching only its own
files. `README-MAC.txt` has more detail.

**How well tested:** the mount test passes on Apple Silicon and Intel Macs with FUSE-T, but MLV Drive
hasn't yet been used with Resolve on a Mac. If you try it, please report what happens.

## Linux

```bash
sudo apt install fuse3          # or: dnf install fuse3, pacman -S fuse3
tar xzf MLV-Drive-*-linux-x64.tar.gz
cd MLV-Drive-*-linux-x64
./MLVDrive
```

Your clips appear in **~/mlvdrive**, from anything mounted under `/media` or `/run/media`. Press
Ctrl+C to stop.

## Options (macOS and Linux)

```
MLVDrive [--mount DIR] [--folder DIR]... [--no-volumes] [--fps N]
```

| Option | What it does |
|---|---|
| `--mount DIR` | Where the drive appears |
| `--folder DIR` | Also show this folder. You can repeat it |
| `--no-volumes` | Show only `--folder` folders, not every mounted drive |
| `--fps N` | Override the frame rate written into the DNGs |

Set `MLVDRIVE_LIBFUSE` to a library's path to choose which FUSE library is used.

## Supported footage

- Uncompressed raw MLV at 10, 12 or 14 bit, including clips split into `.M00`, `.M01` and so on.
- Lossless (LJ92) raw MLV, as recorded by DIGIC 5 bodies and the 6D Mark II port, or made by
  `mlv_dump -c`. Frames decode as they're read, at about 35-50 frames a second per thread at 2K.
- Audio recorded in the clip, or the 6D Mark II port's scratch WAV, lined up with frame 1. If
  the WAV carries LTC timecode, the DNGs and the WAV are stamped with it.
- Colour calibration for the Canon bodies Magic Lantern runs on, including DIGIC 6, 7 and 8. For a
  camera it doesn't know, it uses the matrix in the clip, or failing that the 5D Mark III's.
- **Not yet:** LZMA-compressed clips.
- **Dual ISO:** not processed. Those clips open with their alternating bright and dark line pairs. Process them in
  [MLV App](https://mlv.app) instead.
- **Focus pixels:** not corrected. A flat test take from the 6D Mark II's 1080p mode showed no focus-pixel pattern,
  but bodies that record one will show it.

A clip that can't be shown has a `_why-is-this-empty.txt` in its folder saying why.

## Command line

`mlvdrive-cli` is in every download and works without the drive or any FUSE library. If you can't
install a driver, `export` writes real files instead.

```
mlvdrive-cli info CLIP.MLV                         what's in a clip
mlvdrive-cli export CLIP.MLV OUTDIR [--fps 23.976]  write the DNG folder and WAV to disk
mlvdrive-cli testclip OUTDIR [--bpp 12]             make a colour-bar clip for checking an editor
mlvdrive-cli drift CLIP.MLV [--step 10]             camera clock against the scratch WAV's LTC
```

## Troubleshooting

| Problem | Try this |
|---|---|
| A clip's folder is empty | Read `_why-is-this-empty.txt` in that folder |
| My clips don't appear | Windows: use **Look for clips again**, or **Also show a folder...** for the Windows drive. macOS and Linux: use `--folder` for anything not mounted as a drive |
| "Safely remove" says the drive is busy | Close the clips in Resolve. MLV Drive lets go of a clip about 10 seconds after the last read |
| M: is already taken | Tray menu > **Drive letter** |
| The folder "is not empty" on macOS or Linux | Stop the other copy, or choose another `--mount` folder |
| A clip's folder says "no complete frames" | The take stopped before its first frame was saved, so the clip is empty. A 1 KB `.MLV` confirms it |
| Linux or macOS says it needs FUSE | Install it as shown above, or set `MLVDRIVE_LIBFUSE` |
| macOS says the app "cannot be opened" | Run `codesign --force --sign - bin/arm64/MLVDrive` in the download folder (`bin/x64` on Intel) |
| The colours look wrong | Check the log, which says when a camera wasn't recognised |

On Windows, the log is under **Open log** in the tray menu. On macOS and Linux, it prints in the terminal.

## Building from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The whole solution
builds on Windows, macOS and Linux.

```bash
dotnet test tests/MlvDrive.Tests
```

| Task | Command |
|---|---|
| Windows zip (Windows only) | `powershell -File build\Package.ps1 [-Rid win-arm64]` |
| macOS and Linux packages (any OS, Python 3) | `python3 build/PackageUnix.py` |
| Mount a test clip and check it byte for byte | `bash build/smoke.sh MLVDRIVE_BINARY CLI_BINARY` |

Each release is built from a tagged commit, so its exact source is on the
[releases page](https://github.com/charlie-schomp/MLVDrive/releases).

The code is split into `src/MlvDrive.Core` (reading MLV, writing DNG, no platform code),
`src/MlvDrive.Windows` (WinFsp and the tray app), `src/MlvDrive.Fuse` (macOS and Linux) and
`src/MlvDrive.Cli`.

## Credits and licence

GNU GPL, version 2 or later (`GPL-2.0-or-later`); see [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

**WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos**: <https://github.com/winfsp/winfsp>

The DNG tag layout, the colour matrices of older Canon bodies (credited there to Andy600,
from Adobe DNG Converter) and the white-balance maths come from David Milligan's
[MLVFS](https://github.com/davidmilligan/MLVFS), which is GPL v2 or later. Newer bodies' matrices are from
[LibRaw](https://github.com/LibRaw/LibRaw). The idea of patching MLVFS for Resolve comes from
[MLV Resolve FS](https://github.com/SpicyBoi82/MLVS-is-Davinci-Resolve-magic-lantern-); this is a
separate implementation.
