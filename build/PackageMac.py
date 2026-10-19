"""Zip the macOS test build with Unix permissions (exec bits survive the download).
Run after: dotnet publish src/MlvDrive.Fuse -c Release -r osx-arm64|osx-x64 -o artifacts/fuse-osx-<arch>"""
import os, zipfile, hashlib
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
name = "MLV-Drive-mac-0.1.0"
out = os.path.join(root, "artifacts", name + ".zip")
files = [
    ("installer/mac/MLV Drive.command", "MLV Drive.command", 0o755),
    ("installer/mac/README-MAC.txt", "README-MAC.txt", 0o644),
    ("LICENSE", "LICENSE", 0o644),
    ("artifacts/fuse-osx-arm64/MLVDrive", "bin/arm64/MLVDrive", 0o755),
    ("artifacts/fuse-osx-x64/MLVDrive", "bin/x64/MLVDrive", 0o755),
]
sums = []
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for src, dst, mode in files:
        data = open(os.path.join(root, src), "rb").read()
        if dst.endswith((".command", ".txt")):
            data = data.replace(b"\r\n", b"\n")
        info = zipfile.ZipInfo(f"{name}/{dst}")
        info.external_attr = (0o100000 | mode) << 16
        info.create_system = 3
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, data)
        sums.append(f"{hashlib.sha256(data).hexdigest()}  {dst}")
    info = zipfile.ZipInfo(f"{name}/SHA256SUMS.txt"); info.external_attr = (0o100644) << 16; info.create_system = 3
    z.writestr(info, "\n".join(sums) + "\n")
print(out, os.path.getsize(out) // (1024 * 1024), "MB")
print("\n".join(sums))
