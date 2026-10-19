MLV Drive for macOS - TEST BUILD 0.1.0 (never run on a Mac before; tested on Linux FUSE)

1. Install FUSE-T (free, no kernel extension, no security changes):
     https://www.fuse-t.org   or   brew install macos-fuse-t/homebrew-cask/fuse-t
   (macFUSE also works if you already have it.)
2. Open Terminal, then:
     cd ~/Downloads/MLV-Drive-mac-0.1.0
     bash "MLV Drive.command"
   (Double-clicking the .command file also works after right-click > Open once.)
3. Clips on every mounted volume (card, external drive) appear in ~/MLV Drive as
   <volume>/<folders>/<clip>/ with CinemaDNG frames + the matching WAV.
4. In Resolve: Media page > Media Storage > add/browse ~/MLV Drive, drag clip folders
   into the media pool (from Resolve's browser, not Finder - same as on Windows).
5. Stop: Ctrl+C in the Terminal window (it unmounts itself).

Options: --mount DIR, --folder DIR (extra folder), --no-volumes, --fps 23.976

Please report: does it start? does ~/MLV Drive list the clips? do frames import in
Resolve with correct colour? Copy the Terminal output if anything fails. If macOS says
the app "cannot be opened", run:  codesign --force --sign - bin/arm64/MLVDrive
