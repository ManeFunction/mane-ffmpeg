#!/usr/bin/env python3
"""
Fail unless the release tag matches the project's version. Works for CI (copy it to
.github/scripts/check_version.py) and locally.

The tag comes from --tag or the GITHUB_REF_NAME environment variable (e.g. "v1.3.0"); a
leading "v" is ignored. The version is read from the first source that exists:

  1. --file PATH --regex RE   (RE needs one capture group; use for anything unusual)
  2. pyproject.toml           static [project] version = "x.y.z"
  3. pyproject.toml           dynamic version from [tool.setuptools.dynamic] version = {attr = "pkg.version"}:
                              read from src/pkg/__init__.py, pkg/__init__.py, src/pkg.py or pkg.py
  4. package.json             "version"
  5. Cargo.toml               [package] version
  6. VERSION                  the whole file

Pure standard library, Python 3.9+ (a regex reader is used instead of tomllib).
Exit code 0 = match, 1 = mismatch or no version found.

Usage:
    check_version.py [--tag TAG] [--file PATH --regex RE] [--root DIR]
"""
import argparse
import json
import os
import re
import sys


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def toml_section(text, name):
    """Body of a [name] table (up to the next table header), or ''."""
    m = re.search(rf"^\[{re.escape(name)}\]\s*$(.*?)(?=^\[|\Z)", text, re.MULTILINE | re.DOTALL)
    return m.group(1) if m else ""


def find_version(root, file=None, regex=None):
    """Return (version, source description) or (None, reason)."""
    path = lambda *p: os.path.join(root, *p)  # noqa: E731
    if file:
        m = re.search(regex, read(path(file)), re.MULTILINE)
        return (m.group(1), f"{file} /{regex}/") if m else (None, f"no match for /{regex}/ in {file}")

    if os.path.exists(path("pyproject.toml")):
        text = read(path("pyproject.toml"))
        m = re.search(r'^version\s*=\s*"([^"]+)"', toml_section(text, "project"), re.MULTILINE)
        if m:
            return m.group(1), "pyproject.toml [project] version"
        m = re.search(r'^version\s*=\s*\{\s*attr\s*=\s*"([^"]+)"\s*\}',
                      toml_section(text, "tool.setuptools.dynamic"), re.MULTILINE)
        if m:
            module, _, attr = m.group(1).rpartition(".")
            top = module.replace(".", "/")
            for cand in (f"src/{top}/__init__.py", f"{top}/__init__.py", f"src/{top}.py", f"{top}.py"):
                if os.path.exists(path(cand)):
                    mm = re.search(rf'^{re.escape(attr)}\s*=\s*"([^"]+)"', read(path(cand)), re.MULTILINE)
                    if mm:
                        return mm.group(1), f"{cand} {attr}"
            return None, f"pyproject.toml points to {m.group(1)} but no matching source file has it"

    if os.path.exists(path("package.json")):
        v = json.loads(read(path("package.json"))).get("version")
        if v:
            return v, "package.json version"

    if os.path.exists(path("Cargo.toml")):
        m = re.search(r'^version\s*=\s*"([^"]+)"', toml_section(read(path("Cargo.toml")), "package"),
                      re.MULTILINE)
        if m:
            return m.group(1), "Cargo.toml [package] version"

    if os.path.exists(path("VERSION")):
        return read(path("VERSION")).strip(), "VERSION"

    return None, "no version source found (pass --file and --regex)"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--tag", default=os.environ.get("GITHUB_REF_NAME", ""))
    ap.add_argument("--file")
    ap.add_argument("--regex")
    ap.add_argument("--root", default=".")
    args = ap.parse_args()

    if bool(args.file) != bool(args.regex):
        print("::error::--file and --regex go together", file=sys.stderr)
        return 1
    version, source = find_version(args.root, args.file, args.regex)
    if version is None:
        print(f"::error::Could not read the project version: {source}.")
        return 1
    tag = args.tag[1:] if args.tag.startswith("v") else args.tag
    if tag != version:
        print(f"::error::Tag {args.tag!r} does not match the version {version!r} from {source}. "
              "Bump the version (or retag) before releasing.")
        return 1
    print(f"Tag {args.tag} matches version {version} ({source}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
