# MLV Drive

Edit Magic Lantern raw video (`.MLV`) in DaVinci Resolve with **no conversion and no extra
program to open**. MLV Drive runs quietly in the system tray and keeps a virtual drive,
**M:**, where every clip on your connected drives appears as a CinemaDNG sequence plus its
audio. Drag a clip from M: into Resolve and you get Resolve's full Camera Raw controls
(white balance, ISO/exposure, highlight recovery, decode quality, colour space).

Nothing is converted or copied: frames are produced on the fly as Resolve reads them, and
your footage is never written to.

## How it works day to day

1. Copy clips from the camera's card to your external drive (or just leave the card in a reader).
2. Plug the drive in. MLV Drive notices and shows a notification.
3. In Resolve, open the Media page, go to **M:\\\<drive name>\\...** and drag clip folders in.

The paths on M: stay the same every time you connect the same drive, so projects relink.
Clips still being copied appear as their frames arrive. When nothing is reading a clip,
MLV Drive lets go of it, so "Safely remove" works normally.

What M: shows:

```
M:\SSD 2TB\DCIM\100CANON\M08-1651\
    M08-1651_000000.dng
    M08-1651_000001.dng
    ...
    M08-1651.wav        audio recorded in the clip, or the 6D Mark II port's scratch WAV
```

A clip that can't be shown has a `_why-is-this-empty.txt` in its folder saying why.

## Install (Windows 10/11, 64-bit)

Download `MLV-Drive-<version>.zip`, unzip it, and double-click **INSTALL.cmd**.

- MLV Drive is installed for your account only, in `%LOCALAPPDATA%\Programs\MLV Drive`, and
  set to start with Windows. No administrator rights are needed for it.
- It needs **WinFsp**, the free virtual-drive driver from <https://winfsp.dev>. If it isn't
  installed, the installer installs the pinned release 2.1.25156 using winget, or from
  WinFsp's GitHub release, accepted only if its SHA-256 matches the value pinned in the
  installer and it is validly signed by WinFsp's publisher. This is the one step where
  Windows asks for permission.
- Nothing is compiled on your PC, and nothing else is downloaded.

To remove it, run **UNINSTALL.cmd**. WinFsp stays installed; remove it from Settings > Apps
if you want.

`MLVDrive.exe` is not code-signed yet, so Windows SmartScreen may warn the first time.
`SHA256SUMS.txt` in the zip lists every file's checksum.

## Tray menu

Right-click the tray icon:

- **Open drive**.
- **Look for clips again**.
- **Frame rate in DNGs**: for clips whose recorded rate is wrong.
- **Drive letter**.
- **Also show a folder...**: for folders on the Windows drive, which isn't scanned by default.
- **Start with Windows**, **Notifications**.
- **Open log**.

## Supported footage

- Uncompressed raw MLV at 10, 12 or 14 bit, including clips split into `.M00`, `.M01`... files.
- Colour calibration for the Canon bodies Magic Lantern runs on, including the 6D Mark II and
  other DIGIC 6/7/8 cameras. For a camera it doesn't know, it uses the matrix the camera wrote
  into the clip, and otherwise the 5D Mark III's. The log says when that happens.
- Lossless (LJ92) raw MLV, as recorded by DIGIC 5 bodies or made by `mlv_dump -c`. Frames are
  decoded as the editor reads them and shown as the same uncompressed DNGs. Decoding runs
  about 35-50 frames a second per thread at 2K, and editors read several frames at once.
- Not yet: LZMA-compressed clips. Their folders explain this.

## Command line

`mlvdrive` (in `src/MlvDrive.Cli`) works without the drive:

```
mlvdrive info CLIP.MLV
mlvdrive export CLIP.MLV OUTDIR [--fps 23.976]   write the DNG folder + WAV to disk
mlvdrive testclip OUTDIR [--bpp 12]              synthetic colour-bar clip for checking an editor
```

## Building

.NET 8 SDK on Windows: `dotnet test`, then `powershell -File build\Package.ps1` for the
release zip. The core (`src/MlvDrive.Core`) has no Windows dependencies.

## Credits and licence

GPL v2. The DNG tag layout, the colour matrices of older Canon bodies (credited there to
Andy600, from Adobe DNG Converter) and the white-balance maths come from David Milligan's
[MLVFS](https://github.com/davidmilligan/MLVFS), which is GPL v2. Newer bodies' matrices are
from [LibRaw](https://github.com/LibRaw/LibRaw). The idea of patching MLVFS for Resolve comes
from [MLV Resolve FS](https://github.com/SpicyBoi82/MLVS-is-Davinci-Resolve-magic-lantern-);
this is a separate implementation.
