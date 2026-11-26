"""Write the v0.7 release manifest with exact sizes and SHA-256 checksums."""
import argparse
import hashlib
import json
import os
import time


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", required=True)
    ap.add_argument("--studio", required=True)
    ap.add_argument("--zip", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--commit-github", default="")
    ap.add_argument("--commit-gitlab", default="")
    ap.add_argument("--tag", default="v0.7-alpha")
    args = ap.parse_args()
    items = []
    for name, path in (("integrated-mesen", args.mesen), ("standalone-studio", args.studio),
                       ("portable-zip", args.zip)):
        items.append({
            "name": name,
            "path": os.path.abspath(path),
            "bytes": os.path.getsize(path),
            "sha256": sha256(path),
        })
    manifest = {
        "release": "v0.7-alpha",
        "schemaVersion": "1.0.0",
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "tag": args.tag,
        "repositories": {
            "github": {"repo": "Bsenzio/MesenCE", "branch": "hdstudio", "commit": args.commit_github},
            "gitlab": {"repo": "senziosavino_b/RetroArcDo_HDStudio", "branch": "main", "commit": args.commit_gitlab},
        },
        "artifacts": items,
        "license": "GPLv3",
        "originalRomIntegrity": "ROM bytes are never modified; per-row SHA-256 checks are in replacement-results.json",
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1)
    print(json.dumps(manifest, indent=1))


if __name__ == "__main__":
    main()
