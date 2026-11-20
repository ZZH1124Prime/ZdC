import importlib.util
import io
from pathlib import Path, PurePosixPath
import tarfile
import tempfile
import unittest

script = Path(__file__).resolve().parents[1] / "linux" / "build_deb.py"
spec = importlib.util.spec_from_file_location("build_deb", script)
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class PackageTest(unittest.TestCase):
    def test_install_directories_precede_their_files(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "zdc.deb"
            builder.build(package)
            archive = io.BytesIO(package.read_bytes())
        self.assertEqual(archive.readline(), b"!<arch>\n")
        members = {}
        while header := archive.read(60):
            self.assertEqual(header[58:], b"`\n")
            length = int(header[48:58])
            members[header[:16].decode().strip().rstrip("/")] = archive.read(length)
            archive.read(length % 2)
        self.assertEqual(list(members), ["debian-binary", "control.tar.gz", "data.tar.gz"])
        self.assertEqual(members["debian-binary"], b"2.0\n")
        with tarfile.open(fileobj=io.BytesIO(members["data.tar.gz"]), mode="r:gz") as data:
            directories = {PurePosixPath(".")}
            for item in data:
                name = PurePosixPath(item.name)
                self.assertIn(name.parent, directories, item.name)
                if item.isdir():
                    directories.add(name)
            self.assertEqual(data.getmember("./usr/bin/zdc").mode, 0o755)
            self.assertEqual(data.extractfile("./usr/bin/zdc").readline(), b"#!/usr/bin/python3\n")


if __name__ == "__main__":
    unittest.main()
