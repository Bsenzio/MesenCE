$ErrorActionPreference = 'Stop'
$root = 'D:\MesenHighWork\validation-v071'
$fullPid = 5176
while (Get-Process -Id $fullPid -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 30 }
Set-Location 'D:\MesenHighWork'
$exe = 'D:\MesenHigh\bin\win-x64\Release\Mesen.exe'

# Rebuild the UI with the save-RAM isolation fix after the full-library batch exits.
& cmd /c "D:\MesenHighWork\build.cmd ui *> `"$root\build-ui.log`""

# Serial state re-verification of every mismatch/failed-context row.
python -u D:\MesenHighWork\scripts\v071_state_reverify.py --results "$root\full-library\replacement-results.json" --inventory D:\MesenHighWork\validation-v07\inventory\library-inventory.json --out "$root\state-reverification.json" --mesen $exe *> "$root\state-reverify-final.log"

# Background replacement pass (validation-split sample, 2x, short work root).
python -u D:\MesenHigh\scripts\hdstudio_v071_autoreplace.py --inventory D:\MesenHighWork\validation-v07\inventory\library-inventory.json --hash-file "$root\bg-selection-hashes.txt" --out-root "$root\background" --target-frames=900 --scale=2 --workers=7 --verify-scenes=2 --allow-shared --chrram-conditions=position --target-mode=background --short-work --mesen $exe *> "$root\background.log"

# A bounded second scene attempt for failures that had no visible sprite at frame 900.
python -u D:\MesenHigh\scripts\hdstudio_v071_autoreplace.py --inventory D:\MesenHighWork\validation-v07\inventory\library-inventory.json --hash-file "$root\late-hashes.txt" --out-root "$root\late" --target-frames=2600 --scale=2 --workers=7 --verify-scenes=1 --allow-shared --chrram-conditions=position --short-work --mesen $exe *> "$root\late.log"

# Merge the main run with the short-path, Batman and late-scene overrides, then state fixes.
python D:\MesenHighWork\scripts\v071_merge.py --base "$root\full-library\replacement-results.json" --override "$root\short-consolidated.json" --override "$root\batman\replacement-results.json" --override "$root\late\replacement-results.json" --state "$root\state-reverification.json" --out "$root\final\replacement-results.json"

# Master matrix and metrics.
python D:\MesenHighWork\scripts\v071_build_matrix.py --inventory D:\MesenHighWork\validation-v07\inventory\library-inventory.json --results "$root\final\replacement-results.json" --background "$root\background\replacement-results.json" --animation "$root\animation-results.json" --out "$root\master-matrix"
python D:\MesenHighWork\scripts\v071_metrics.py --matrix-summary "$root\master-matrix-summary.json" --results "$root\final\replacement-results.json" --background "$root\background\replacement-results.json" --animation "$root\animation-results.json" --integrity "$root\library-integrity.json" --state "$root\state-reverification.json" --out "$root\metrics.json"
Write-Output 'POST_FULL_DONE'
