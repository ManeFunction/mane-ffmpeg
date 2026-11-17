#!/usr/bin/env python3
"""
Print a release's whole section from CHANGELOG.md, copied verbatim: everything between the
"## [x.y.z] - date" heading and the next "## " heading (the intro, if any, and every "### Added/Changed/
Fixed/..." block). release.yml adds the Downloads notes and the Full Changelog link after it.

The tag comes from --tag or GITHUB_REF_NAME (a leading "v" is ignored when matching the CHANGELOG).
Pure standard library, Python 3.9+. A version missing from CHANGELOG.md is a warning, not an error, and
gives an empty body (check_version.py is the gate).

Usage:
    release_notes.py [--tag v1.2.3] [--changelog CHANGELOG.md]
"""
import argparse
import os
import re
import sys


def changelog_section(text, version):
    """Everything between the version's "## " heading and the next "## " heading."""
    heading = re.compile(r"^##\s+\[?v?" + re.escape(version) + r"\]?(?:\s|$)")
    lines = text.splitlines()
    for i, line in enumerate(lines):
        if heading.match(line):
            body = []
            for following in lines[i + 1:]:
                if following.startswith("## "):
                    break
                body.append(following)
            return "\n".join(body).strip("\n").rstrip(), True
    return "", False


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tag", default=os.environ.get("GITHUB_REF_NAME", ""))
    parser.add_argument("--changelog", default="CHANGELOG.md")
    args = parser.parse_args()
    if not args.tag:
        parser.error("no tag: pass --tag or set GITHUB_REF_NAME")
    version = args.tag[1:] if args.tag.startswith("v") else args.tag

    section, found = "", False
    if os.path.exists(args.changelog):
        with open(args.changelog, encoding="utf-8") as f:
            section, found = changelog_section(f.read(), version)
    if not found:
        print(f"warning: no '## [{version}]' section in {args.changelog}", file=sys.stderr)

    print(section)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
