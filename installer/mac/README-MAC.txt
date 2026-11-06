MLV Drive for macOS (Apple Silicon and Intel)

The mount test passes on Apple Silicon and Intel with FUSE-T, but this has not yet been
used with Resolve on a Mac. If you try it, please report what happens.

1. Install FUSE-T (free, no kernel extension, no security changes):
     https://www.fuse-t.org   or   brew install macos-fuse-t/homebrew-cask/fuse-t
   (macFUSE also works if you already have it.)
2. Open Terminal in this folder, then:
     bash "MLV Drive.command"
   (Double-clicking the .command file also works, if macOS allows it.)
3. Clips on every mounted volume (card, external drive) appear in ~/MLV Drive as
   <volume>/<folders>/<clip>/ with CinemaDNG frames and the matching WAV.
4. In Resolve: Media page > Media Storage > add ~/MLV Drive, then drag clip folders into
   the media pool from Resolve's browser, not Finder.
5. To stop: press Ctrl+C in the Terminal window. It unmounts itself.

Options: --mount DIR, --folder DIR (extra folder), --no-volumes, --fps 23.976

Command-line tool, no FUSE needed:  bin/arm64/mlvdrive-cli  (bin/x64 on Intel)

If something fails, copy the Terminal output into your report. If macOS says the app
"cannot be opened", run:  codesign --force --sign - bin/arm64/MLVDrive   (bin/x64 on Intel)
