"""Generate a human-readable v0.5 smoke-validation summary from measured JSON."""
import argparse
import collections
import json
import os


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    rows = json.load(open(args.results, encoding="utf-8"))
    capture_ok = [r for r in rows if r.get("captureCompleted") == "passed"]
    failed = [r for r in rows if r.get("captureCompleted") != "passed"]
    gameplay = [r for r in rows if r.get("gameplayReached") == "passed"]
    blocked = [r for r in rows if r.get("gameplayReached") == "blocked"]
    chrrom = [r for r in rows if r.get("chrType") == "CHR-ROM"]
    chrram = [r for r in rows if r.get("chrType") == "CHR-RAM"]
    by_mapper = collections.Counter(str(r.get("mapper")) for r in rows)
    import re
    unique_titles = len({(r.get("gameTitle") or "").strip().lower() for r in rows})
    normalized_titles = len({
        re.sub(r"\s*\([^()]*(USA|Japan|Europe|World|Asia|En|Ja|Rev|Pirate|FamicomBox|Namcot|Collection|Animal Crossing|Castlevania Anniversary)[^()]*\)", "", (r.get("gameTitle") or ""), flags=re.I).strip().lower()
        for r in rows
    })
    total_seconds = sum(r.get("processingSeconds") or 0 for r in rows)
    patterns = sum(r.get("observedPatterns") or 0 for r in capture_ok)
    compositions = sum(r.get("reconstructedCompositions") or 0 for r in capture_ok)
    backgrounds = sum(r.get("backgroundCompositions") or 0 for r in capture_ok)
    occurrences = sum(r.get("spriteOccurrences") or 0 for r in capture_ok)

    lines = []
    lines.append("# v0.5 Tier-1 library validation\n")
    lines.append("Generated from the machine-readable `smoke-results.json`. "
                 "This is a 300-frame compatibility pass, not a semantic-completeness claim.\n")
    lines.append("| Metric | Result |")
    lines.append("|---|---:|")
    lines.append(f"| Unique ROM hashes | **{len(rows)}** |")
    lines.append(f"| Unique measured filename titles | **{unique_titles}** |")
    lines.append(f"| Normalized base game titles (region/revision suffixes ignored) | **{normalized_titles}** |")
    lines.append(f"| Capture completed | **{len(capture_ok)}** |")
    lines.append(f"| Capture/load failed | **{len(failed)}** |")
    lines.append(f"| Asset catalog exported | **{sum(r.get('assetsExported') == 'passed' for r in rows)}** |")
    lines.append(f"| Generic gameplay detected | **{len(gameplay)}** |")
    lines.append(f"| Gameplay signature not detected | **{len(blocked)}** |")
    lines.append(f"| CHR-ROM rows | **{len(chrrom)}** |")
    lines.append(f"| CHR-RAM rows | **{len(chrram)}** |")
    lines.append(f"| Sum of observed patterns | **{patterns:,}** |")
    lines.append(f"| Sum of reconstructed compositions | **{compositions:,}** |")
    lines.append(f"| Sum of background compositions | **{backgrounds:,}** |")
    lines.append(f"| Sum of composition occurrences | **{occurrences:,}** |")
    lines.append(f"| Total processing time | **{total_seconds / 60:.1f} min** |")
    lines.append("")
    lines.append("## Mapper coverage\n")
    lines.append("| Mapper | ROMs |")
    lines.append("|---:|---:|")
    for mapper, count in sorted(by_mapper.items(), key=lambda kv: (-kv[1], kv[0])):
        lines.append(f"| {mapper} | {count} |")
    lines.append("")
    if failed:
        lines.append("## Failed ROM loads\n")
        lines.append("| ROM | Mapper | Error |")
        lines.append("|---|---:|---|")
        for r in failed:
            error = (r.get("error") or "").replace("\n", " ").strip()
            if len(error) > 180:
                error = error[-180:]
            lines.append(f"| {r.get('romName','')} | {r.get('mapper')} | {error} |")
        lines.append("")
    lines.append("## Per-ROM matrix\n")
    lines.append("| ROM | SHA-256 | Mapper | CHR | Capture | Gameplay | Frames | Patterns | Compositions | Backgrounds | Integrity |")
    lines.append("|---|---|---:|---|---|---|---:|---:|---:|---:|---|")
    for r in rows:
        lines.append("| {rom} | `{sha}` | {mapper} | {chr} | {cap} | {game} | {frames} | {pat} | {comp} | {bg} | {romok} |".format(
            rom=str(r.get("romName", "")).replace("|", "\\|"),
            sha=(r.get("sha256") or "")[:16],
            mapper=r.get("mapper"),
            chr=r.get("chrType"),
            cap=r.get("captureCompleted"),
            game=r.get("gameplayReached"),
            frames=r.get("framesProcessed") or "-",
            pat=r.get("observedPatterns") if r.get("observedPatterns") is not None else "-",
            comp=r.get("reconstructedCompositions") or 0,
            bg=r.get("backgroundCompositions") or 0,
            romok=r.get("originalRomIntegrity")))
    lines.append("")
    lines.append("Replacement, animation, background-replacement, context and stock-Mesen columns are intentionally "
                 "`not tested` in this Tier-1 dataset. They are reported separately for the deep and authored-replacement tiers.\n")
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    open(args.out, "w", encoding="utf-8").write("\n".join(lines))
    print(args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
