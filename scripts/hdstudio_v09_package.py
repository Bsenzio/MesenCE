"""Build the final v0.9 portable package and checksum manifest."""
import argparse
import hashlib
import json
import os
import shutil
import time
import zipfile


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def copy_tree(src, dst):
    if not os.path.isdir(src):
        return
    for folder, _, files in os.walk(src):
        rel = os.path.relpath(folder, src)
        target = os.path.join(dst, rel) if rel != "." else dst
        os.makedirs(target, exist_ok=True)
        for name in files:
            shutil.copy2(os.path.join(folder, name), os.path.join(target, name))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bin", default=r"D:\MesenHigh\bin\win-x64\Release")
    ap.add_argument("--template", default=r"D:\MesenHighWork\dist\RetroArcDoHDStudio-v0.9")
    ap.add_argument("--out", default=r"D:\MesenHighWork\dist\RetroArcDoHDStudio-v0.9-final")
    ap.add_argument("--zip", default=r"D:\MesenHighWork\dist\RetroArcDoHDStudio-v0.9-final-win-x64.zip")
    ap.add_argument("--docs", default=r"D:\MesenHighWork\docs")
    ap.add_argument("--manifest", default=r"D:\MesenHighWork\validation-v09\final-release-manifest.json")
    args = ap.parse_args()
    if os.path.isdir(args.out):
        shutil.rmtree(args.out)
    if os.path.isdir(args.template):
        copy_tree(args.template, args.out)
    else:
        os.makedirs(args.out, exist_ok=True)
    #Overwrite the executable/dependency files with the actual final build.
    for name in os.listdir(args.bin):
        src = os.path.join(args.bin, name)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(args.out, name))
    #A standalone Studio launcher is the same binary; keeping a distinct name is
    #the documented package convention.
    shutil.copy2(os.path.join(args.bin, "Mesen.exe"), os.path.join(args.out, "RetroArcDoHDStudio.exe"))
    doc_names = [
        "FINAL_ENGINEERING_REPORT_v0.9.md", "VALIDATION_v0.9.md",
        "RETROARCHAEOLOGY.md", "ALGORITHM_PROVENANCE.md",
        "THIRD_PARTY_COMPONENTS.md", "PRIOR_ART_INTEGRATION_v0.9.md",
    ]
    for name in doc_names:
        src = os.path.join(args.docs, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(args.out, name))
    #Deterministic zip: sorted paths, fixed timestamps.
    if os.path.exists(args.zip):
        os.remove(args.zip)
    with zipfile.ZipFile(args.zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for folder, _, files in os.walk(args.out):
            for name in sorted(files):
                path = os.path.join(folder, name)
                rel = os.path.relpath(path, args.out)
                info = zipfile.ZipInfo(rel, date_time=(2026, 11, 28, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(path, "rb") as f:
                    z.writestr(info, f.read(), compresslevel=9)
    manifest = {
        "version": "0.9",
        "status": "final-engine-evidence-package",
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "integratedMesen": {
            "path": os.path.join(args.out, "Mesen.exe"),
            "bytes": os.path.getsize(os.path.join(args.out, "Mesen.exe")),
            "sha256": sha256(os.path.join(args.out, "Mesen.exe")),
        },
        "standaloneStudio": {
            "path": os.path.join(args.out, "RetroArcDoHDStudio.exe"),
            "bytes": os.path.getsize(os.path.join(args.out, "RetroArcDoHDStudio.exe")),
            "sha256": sha256(os.path.join(args.out, "RetroArcDoHDStudio.exe")),
        },
        "portableZip": {
            "path": args.zip,
            "bytes": os.path.getsize(args.zip),
            "sha256": sha256(args.zip),
        },
        "docs": [n for n in doc_names if os.path.exists(os.path.join(args.out, n))],
    }
    os.makedirs(os.path.dirname(args.manifest), exist_ok=True)
    with open(args.manifest, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1)
    print(json.dumps(manifest, indent=1))


if __name__ == "__main__":
    main()
