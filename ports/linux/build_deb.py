"""Build the all-architecture .deb with Python 3.8+ on Linux or Windows."""
import argparse
import gzip
import io
from pathlib import Path, PurePosixPath
import tarfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
VERSION = "0.1.0~preview1"
CONTROL = f"""Package: zdc
Version: {VERSION}
Architecture: all
Maintainer: ZdC contributors <maintainers@zdc.invalid>
Section: net
Priority: optional
Depends: python3 (>= 3.8), python3-gi, gir1.2-gtk-3.0, gir1.2-webkit2-4.1 | gir1.2-webkit2-4.0
Description: ZdC application list and local mihomo rule provider exporter
 Manage Linux application entries, launch programs and generate a local
 classical rule provider for a cooperating proxy client.
"""


def tar_gz(files):
    stream = io.BytesIO()
    with gzip.GzipFile(fileobj=stream, mode="wb", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.GNU_FORMAT) as archive:
            directories = set()
            for name, data, mode in files:
                for parent in reversed(PurePosixPath(name).parents):
                    if parent == PurePosixPath(".") or parent in directories:
                        continue
                    folder = tarfile.TarInfo("./" + str(parent))
                    folder.type, folder.mode = tarfile.DIRTYPE, 0o755
                    folder.uname = folder.gname = "root"
                    archive.addfile(folder)
                    directories.add(parent)
                entry = tarfile.TarInfo("./" + name)
                entry.mode, entry.size = mode, len(data)
                entry.uname = entry.gname = "root"
                archive.addfile(entry, io.BytesIO(data))
    return stream.getvalue()


def build(destination):
    files = [(HERE / "zdc.py", "usr/bin/zdc", 0o755),
             (HERE / "zdc.desktop", "usr/share/applications/zdc.desktop", 0o644),
             (ROOT / "COPYING", "usr/share/doc/zdc/copyright", 0o644),
             (HERE.parent / "shared" / "launcher.png", "usr/share/icons/hicolor/192x192/apps/zdc.png", 0o644),
             (ROOT / "src/ZdC.App/Assets/ZdCMark.png", "usr/share/zdc/ui/ZdCMark.png", 0o644)]
    for name in ("index.html", "style.css", "model.js", "ui.js"):
        files.append((HERE.parent / "shared" / name, "usr/share/zdc/ui/" + name, 0o644))
    data = tar_gz((name, src.read_bytes(), mode) for src, name, mode in files)
    members = [("debian-binary", b"2.0\n"),
               ("control.tar.gz", tar_gz([("control", CONTROL.encode(), 0o644)])),
               ("data.tar.gz", data)]
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("wb") as output:
        output.write(b"!<arch>\n")
        for name, payload in members:
            header = f"{name + '/':16}{0:<12}{0:<6}{0:<6}{'100644':<8}{len(payload):<10}`\n"
            output.write(header.encode("ascii"))
            output.write(payload)
            if len(payload) % 2:
                output.write(b"\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / f"zdc_{VERSION}_all.deb")
    build(parser.parse_args().output)
