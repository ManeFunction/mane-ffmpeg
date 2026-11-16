"""Checks a Windows release zip: the app, the bundled libmpv and the package marker are inside, built for the RID's CPU.

Usage: python check_windows_zip.py <zip> <win-x64|win-arm64>
"""

import struct
import sys
import zipfile

MACHINES = {"win-x64": 0x8664, "win-arm64": 0xAA64}
FOLDER = "Mane FFmpeg/"
REQUIRED = ["ManeFfmpeg.exe", "libmpv-2.dll", ".mane-ffmpeg-package", "THIRD-PARTY-NOTICES.txt"]


def machine(data: bytes) -> int:
    if data[:2] != b"MZ":
        raise ValueError("not a PE file")
    offset = struct.unpack_from("<I", data, 0x3C)[0]
    if data[offset:offset + 4] != b"PE\0\0":
        raise ValueError("no PE header")
    return struct.unpack_from("<H", data, offset + 4)[0]


def main() -> int:
    path, rid = sys.argv[1], sys.argv[2]
    expected = MACHINES[rid]
    with zipfile.ZipFile(path) as archive:
        names = set(archive.namelist())
        missing = [name for name in REQUIRED if FOLDER + name not in names]
        if missing:
            print(f"{path} is missing {', '.join(missing)} (has {sorted(names)})")
            return 1
        for name in ("ManeFfmpeg.exe", "libmpv-2.dll"):
            with archive.open(FOLDER + name) as file:
                found = machine(file.read(4096))
            print(f"{name}: machine 0x{found:04x}")
            if found != expected:
                print(f"{name} is built for 0x{found:04x}, not {rid} (0x{expected:04x})")
                return 1
    print(f"{path}: ok for {rid}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
