"""Build the v0.8 portable package and release manifest from the v0.7.1 layout."""
import hashlib
import json
import os
import shutil
import zipfile

DIST = r"D:\MesenHighWork\dist"
SRC = os.path.join(DIST, "RetroArcDoHDStudio-v0.7.1")
DST = os.path.join(DIST, "RetroArcDoHDStudio-v0.8")
ZIP = os.path.join(DIST, "RetroArcDoHDStudio-v0.8-win-x64.zip")
RELEASE = r"D:\MesenHigh\bin\win-x64\Release"
SCRIPTS = r"D:\MesenHigh\scripts"
WORK_SCRIPTS = r"D:\MesenHighWork\scripts"
DOCS = r"D:\RetroArcDo_HDStudio\docs"
OUT_JSON = r"D:\MesenHighWork\validation-v08\release-manifest.json"


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if os.path.isdir(DST):
        shutil.rmtree(DST)
    shutil.copytree(SRC, DST)
    for name in ("Mesen.exe", "Mesen.dll", "MesenCore.dll"):
        src = os.path.join(RELEASE, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(DST, name))
    shutil.copy2(os.path.join(DST, "Mesen.exe"), os.path.join(DST, "RetroArcDoHDStudio.exe"))
    tools = os.path.join(DST, "Tools")
    os.makedirs(tools, exist_ok=True)
    for name in ("hdstudio_v08_autoreplace.py", "hdstudio_v08_animation_verify.py",
                 "hdstudio_v08_acceptance.py", "hdstudio_v08_build_matrix.py",
                 "hdstudio_v08_regression.py", "hdstudio_v08_merge_results.py",
                 "hdstudio_v08_stage_recovery.py", "test_hdstudio_v08_fixtures.py",
                 "deterministic_input_alt.lua"):
        src = os.path.join(SCRIPTS, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(tools, name))
    for name in ("hdstudio_v08_report.py", "hdstudio_v08_reclassify.py"):
        src = os.path.join(WORK_SCRIPTS, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(tools, name))
    for name in ("FINAL_ENGINEERING_REPORT_v0.8.md", "VALIDATION_v0.8.md", "RELEASE_v0.8.md"):
        src = os.path.join(DOCS, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(DST, name))
    if os.path.exists(ZIP):
        os.remove(ZIP)
    with zipfile.ZipFile(ZIP, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for root, _, files in os.walk(DST):
            for file in files:
                path = os.path.join(root, file)
                z.write(path, os.path.relpath(path, DST))
    artifacts = {
        "integrated-mesen": {"path": os.path.join(DST, "Mesen.exe"),
                             "bytes": os.path.getsize(os.path.join(DST, "Mesen.exe")),
                             "sha256": sha(os.path.join(DST, "Mesen.exe"))},
        "standalone-studio": {"path": os.path.join(DST, "RetroArcDoHDStudio.exe"),
                              "bytes": os.path.getsize(os.path.join(DST, "RetroArcDoHDStudio.exe")),
                              "sha256": sha(os.path.join(DST, "RetroArcDoHDStudio.exe"))},
        "portable-zip": {"path": ZIP, "bytes": os.path.getsize(ZIP), "sha256": sha(ZIP)},
    }
    with open(OUT_JSON, "w", encoding="utf-8") as f:
        json.dump({"version": "0.8", "artifacts": artifacts}, f, indent=1)
    print(json.dumps(artifacts, indent=1))


if __name__ == "__main__":
    main()
