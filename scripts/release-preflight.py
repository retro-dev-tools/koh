import json
from pathlib import Path
import re
import sys


def validate(tag, root=Path(__file__).resolve().parent.parent):
    number = r"(?:0|[1-9][0-9]*)"
    identifier = rf"(?:{number}|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
    match = re.fullmatch(
        rf"(tools|ext)-v({number}\.{number}\.{number}"
        rf"(?:-{identifier}(?:\.{identifier})*)?"
        r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)", tag
    )
    if not match:
        raise ValueError("expected tools-v<semver> or ext-v<semver>")
    kind, version = match.groups()
    if kind == "ext":
        extension = root / "editors/vscode"
        package = json.loads((extension / "package.json").read_text())
        lock = json.loads((extension / "package-lock.json").read_text())
        if any(value != version for value in (
            package["version"], lock["version"], lock["packages"][""]["version"]
        )):
            raise ValueError("tag must match package.json and package-lock.json versions")
    return version


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("usage: python scripts/release-preflight.py <tag>")
        print(validate(sys.argv[1]))
    except (ValueError, KeyError, OSError) as error:
        sys.exit(str(error))
