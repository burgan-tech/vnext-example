#!/usr/bin/env python3
"""Fill `code` (base64) from `location` (.csx) in vNext component JSON files.

The runtime compiles scripts from `code`, never from `location`; the VS Code extension normally
fills `code` on save. This does the same for components authored without the extension.
Idempotent: re-encodes every scriptCode object whose `location` resolves to an existing .csx.

Usage: encode-scripts.py <dir-or-json> [...]   (defaults: the cross-domain-lab component folders)
"""
import base64
import json
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DEFAULT_TARGETS = [
    "core/Workflows/cross-domain-lab",
    "core/Tasks/cross-domain-lab",
    "partner/Workflows/cross-domain-lab",
    "partner/Extensions/cross-domain-lab",
]


def encode_in(node, base_dir, stats):
    if isinstance(node, dict):
        loc = node.get("location")
        if isinstance(loc, str) and loc.endswith(".csx") and "code" in node:
            path = os.path.normpath(os.path.join(base_dir, loc))
            if os.path.isfile(path):
                with open(path, "rb") as f:
                    node["code"] = base64.b64encode(f.read()).decode("ascii")
                stats["encoded"] += 1
            else:
                stats["missing"].append(path)
        for v in node.values():
            encode_in(v, base_dir, stats)
    elif isinstance(node, list):
        for v in node:
            encode_in(v, base_dir, stats)


def process_file(path, stats):
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    before = stats["encoded"]
    encode_in(doc, os.path.dirname(path), stats)
    if stats["encoded"] != before:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=2, ensure_ascii=False)
            f.write("\n")
        stats["files"].append(os.path.relpath(path, ROOT))


def main(argv):
    targets = argv or [os.path.join(ROOT, t) for t in DEFAULT_TARGETS]
    stats = {"encoded": 0, "missing": [], "files": []}
    for t in targets:
        if os.path.isdir(t):
            for name in sorted(os.listdir(t)):
                if name.endswith(".json"):
                    process_file(os.path.join(t, name), stats)
        elif t.endswith(".json"):
            process_file(t, stats)
    for f in stats["files"]:
        print(f"encoded: {f}")
    for m in stats["missing"]:
        print(f"MISSING csx: {m}", file=sys.stderr)
    print(f"{stats['encoded']} script(s) encoded into {len(stats['files'])} file(s)")
    return 1 if stats["missing"] else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
