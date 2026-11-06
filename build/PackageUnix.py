# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (C) 2026 charlie-schomp
"""Builds the macOS zip and the Linux tarballs, on any OS with the .NET 8 SDK. Exec bits
survive because the archives are written here, not by the OS.
Usage: py build/PackageUnix.py"""
import hashlib, io, os, re, shutil, subprocess, tarfile, zipfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
art = os.path.join(root, "artifacts")
version = re.search(r"<Version>(.+?)</Version>", open(os.path.join(root, "Directory.Build.props")).read()).group(1)


def publish(project, rid):
    out = os.path.join(art, "publish", rid, project)
    shutil.rmtree(out, ignore_errors=True)
    subprocess.run(["dotnet", "publish", f"src/MlvDrive.{project}", "-c", "Release", "-r", rid, "-o", out],
                   cwd=root, check=True, stdout=subprocess.DEVNULL)
    return out


def binaries(rid):
    return os.path.join(publish("Fuse", rid), "MLVDrive"), os.path.join(publish("Cli", rid), "mlvdrive-cli")


def read(path):
    data = open(os.path.join(root, path), "rb").read()
    return data.replace(b"\r\n", b"\n") if path.endswith((".command", ".txt", ".md")) else data


def common_docs():
    # WinFsp's licence is only for the Windows downloads
    return [(n, read(p), 0o644) for n, p in (
        ("README.md", "README.md"), ("LICENSE", "LICENSE"), ("THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
        ("licenses/dotnet-LICENSE.txt", "licenses/dotnet-LICENSE.txt"),
        ("licenses/dotnet-ThirdPartyNotices.txt", "licenses/dotnet-ThirdPartyNotices.txt"))]


def with_sums(files):
    """[(name in the archive, data, mode)], plus SHA256SUMS.txt."""
    sums = "".join(f"{hashlib.sha256(d).hexdigest()}  {n}\n" for n, d, _ in files)
    return files + [("SHA256SUMS.txt", sums.encode(), 0o644)]


def mac():
    name = f"MLV-Drive-{version}-macos"
    arm_drive, arm_cli = binaries("osx-arm64")
    x64_drive, x64_cli = binaries("osx-x64")
    files = with_sums([
        ("MLV Drive.command", read("installer/mac/MLV Drive.command"), 0o755),
        ("README-MAC.txt", read("installer/mac/README-MAC.txt"), 0o644),
        *common_docs(),
        ("bin/arm64/MLVDrive", read(arm_drive), 0o755),
        ("bin/arm64/mlvdrive-cli", read(arm_cli), 0o755),
        ("bin/x64/MLVDrive", read(x64_drive), 0o755),
        ("bin/x64/mlvdrive-cli", read(x64_cli), 0o755),
    ])
    out = os.path.join(art, name + ".zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for n, d, mode in files:
            info = zipfile.ZipInfo(f"{name}/{n}", (2026, 1, 1, 0, 0, 0))
            info.external_attr = (0o100000 | mode) << 16
            info.create_system = 3
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, d)
    return out


def linux(arch, rid):
    name = f"MLV-Drive-{version}-linux-{arch}"
    drive, cli = binaries(rid)
    files = with_sums([
        ("MLVDrive", read(drive), 0o755),
        ("mlvdrive-cli", read(cli), 0o755),
        *common_docs(),
    ])
    out = os.path.join(art, name + ".tar.gz")
    with tarfile.open(out, "w:gz") as t:
        for n, d, mode in files:
            info = tarfile.TarInfo(f"{name}/{n}")
            info.size, info.mode = len(d), mode
            t.addfile(info, io.BytesIO(d))
    return out


for path in (mac(), linux("x64", "linux-x64"), linux("arm64", "linux-arm64")):
    print(path, os.path.getsize(path) // (1024 * 1024), "MB")
shutil.rmtree(os.path.join(art, "publish"), ignore_errors=True)
