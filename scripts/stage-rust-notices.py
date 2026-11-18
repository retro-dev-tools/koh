import pathlib
import shutil
import subprocess
import sys


destination = pathlib.Path(sys.argv[1])
sysroot = pathlib.Path(
    subprocess.check_output(["rustc", "--print", "sysroot"], text=True).strip()
)
source = sysroot / "share" / "doc" / "rust"
destination.mkdir(parents=True, exist_ok=True)
shutil.copy2(source / "COPYRIGHT-library.html", destination)
shutil.copytree(source / "licenses", destination / "licenses", dirs_exist_ok=True)
(destination / "compiler-version.txt").write_text(
    subprocess.check_output(["rustc", "--version", "--verbose"], text=True),
    encoding="utf-8",
)
