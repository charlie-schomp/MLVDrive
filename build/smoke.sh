#!/bin/bash
# Mounts a synthetic clip and checks the frames served through the filesystem against the
# same clip exported to disk. Needs FUSE; macOS or Linux.
# Usage: build/smoke.sh MLVDRIVE_BINARY CLI_BINARY
set -u
drive=$1 cli=$2
work=$(mktemp -d)
mnt="$work/mnt"
pid=
finish() {
  [ -n "$pid" ] && kill "$pid" 2>/dev/null
  [ -n "$pid" ] && wait "$pid" 2>/dev/null
  rm -rf "$work"
}
trap finish EXIT
fail() { echo "SMOKE FAILED: $1"; cat "$work/log" 2>/dev/null; exit 1; }

"$cli" testclip "$work/src" >/dev/null || fail "testclip"
touch "$work/src/._T12-BARS.MLV"
"$cli" export "$work/src/T12-BARS.MLV" "$work/exp" >/dev/null || fail "export"

"$drive" --no-volumes --folder "$work/src" --mount "$mnt" >"$work/log" 2>&1 &
pid=$!
served="$mnt/src/T12-BARS"
for _ in $(seq 60); do
  [ -f "$served/T12-BARS_000000.dng" ] && break
  kill -0 "$pid" 2>/dev/null || fail "the drive exited"
  sleep 1
done
[ -f "$served/T12-BARS_000000.dng" ] || fail "no frames after 60 s"

grep -m1 'using' "$work/log"
n=$(ls "$served" | grep -c '\.dng$')
[ "$n" = 48 ] || fail "$n frames, expected 48"
[ -f "$mnt/src/_metadata.csv" ] || fail "no _metadata.csv"
[ ! -e "$mnt/src/._T12-BARS" ] || fail "a ._ sidecar was shown as a clip"
for f in "$work/exp/T12-BARS"/*; do
  cmp -s "$f" "$served/$(basename "$f")" || fail "$(basename "$f") differs from the export"
done
touch "$served/x" 2>/dev/null && fail "the drive accepted a write"

kill -TERM "$pid"
for _ in $(seq 20); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
kill -0 "$pid" 2>/dev/null && fail "did not stop on SIGTERM"
wait "$pid"
code=$?
pid=
[ "$code" = 0 ] || fail "exit code $code"
ls "$mnt" | grep -q . && fail "still mounted after stopping"
echo "smoke test passed: $n frames + WAV byte-identical to the export, read-only, clean unmount"
