#!/bin/bash
# MLV Drive for macOS - TEST BUILD. Shows MLV clips on your volumes as CinemaDNG
# folders in ~/MLV Drive for DaVinci Resolve. Read-only. Close this window or press
# Ctrl+C to stop.
cd "$(dirname "$0")" || exit 1
ARCH=$(uname -m); [ "$ARCH" = "arm64" ] || ARCH=x64
BIN="bin/$ARCH/MLVDrive"

# Downloaded files are quarantined and unsigned; this build is ad-hoc signed here so
# macOS will run it (only this app's own files are touched).
xattr -dr com.apple.quarantine . 2>/dev/null
chmod +x "$BIN"
codesign --force --sign - "$BIN" >/dev/null 2>&1 || echo "note: codesign failed; if macOS refuses to run MLVDrive, see README-MAC.txt"

if [ ! -e /usr/local/lib/libfuse-t.dylib ] && [ ! -e /opt/homebrew/lib/libfuse-t.dylib ] && [ ! -e /usr/local/lib/libfuse.2.dylib ]; then
  echo "MLV Drive needs FUSE-T (free, no kernel extension): https://www.fuse-t.org"
  echo "  or with Homebrew:  brew install macos-fuse-t/homebrew-cask/fuse-t"
  echo "Install it, then run this again."
  read -r -p "Press Return to close." _
  exit 1
fi

open "$HOME/MLV Drive" 2>/dev/null &
"./$BIN" "$@"
