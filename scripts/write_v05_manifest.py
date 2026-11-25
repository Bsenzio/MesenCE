"""Write the v0.5 release manifest with actual checksums."""
import argparse
import hashlib
import json
import os


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--zip", default="")
    ap.add_argument("--examples", default="")
    args = ap.parse_args()
    files = [
        r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe",
        r"D:\MesenHigh\bin\win-x64\Release\RetroArcDoHDStudio.exe",
        r"D:\MesenHigh\bin\win-x64\Release\MesenCore.dll",
    ]
    if args.zip:
        files.append(args.zip)
    if args.examples:
        files.append(args.examples)
    doc = {
        "version": "0.5 Alpha",
        "date": "2026-11-25",
        "files": [],
    }
    for path in files:
        doc["files"].append({
            "path": path,
            "size": os.path.getsize(path),
            "sha256": sha(path),
        })
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    json.dump(doc, open(args.out, "w", encoding="utf-8"), indent=2)
    print(json.dumps(doc, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
