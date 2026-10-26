#!/usr/bin/env python3
"""Offline Task4: strict, numeric-preserving adapter and frozen bounded CLI attempts.

No download, filling, session inference, market synthesis or production activation.
Use a NEW --out directory. run.json remains authoritative for each CLI attempt.
"""
import argparse
from collections import Counter
import csv
from datetime import datetime, timedelta, timezone
from decimal import Decimal
import hashlib
import json
from pathlib import Path
import subprocess

SOURCES = {
    "EURUSD": "860d716745586a638e75992f62269d58beaa97e3d17f8df76ea4617d469818a0",
    "XAUUSD": "21377b316d617a471cf75391f45a7d642d3f95b35f8d30ab22ead0e0fff51b23",
    "US500": "8adfa9186949ec2a3e08774998229ee022ea8b1639c0a456020abcb99733df13",
}
CLASSES = {"EURUSD": "Forex", "XAUUSD": "Metal", "US500": "Index"}
FAMILIES = ("TrendPullback", "DonchianBreakout", "MeanReversion", "MomentumImpulse", "DailyTrend", "IndexDipBuy")
PERIODS = {"IS": ("2012-01-01", "2014-01-01", "2021-01-01"),
           "OOS": ("2019-01-01", "2021-01-01", "2024-01-01"),
           "candidate": ("2022-01-01", "2024-01-01", "2026-01-01")}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def utc(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def adapt(source, out):
    data = out / "data"
    data.mkdir()
    manifest = {"source": "Dukascopy jetta native BID H1; local acquisition handoff; downloader 33a6c6e3de97fab9904afc80a3a2fac8b46d8bfb; NO verified session calendar", "timezone": "UTC", "files": []}
    audit = []
    for symbol, expected in SOURCES.items():
        path = source / "output" / (symbol + "_H1_BID.csv")
        raw = path.read_bytes()
        assert sha(raw) == expected, f"frozen source hash changed: {path}"
        rows = list(csv.reader(raw.decode().splitlines()))
        assert rows.pop(0) == ["timestamp", "open", "high", "low", "close", "volume"]
        target = data / (symbol + "_H1.csv")
        previous = None
        times, gaps, annual, intervals = [], [], Counter(), Counter()
        with target.open("w", newline="") as dest:
            writer = csv.writer(dest, lineterminator="\n")
            writer.writerow(["time", "open", "high", "low", "close", "volume"])
            for line, row in enumerate(rows, 2):
                assert len(row) == 6, (path, line)
                t = utc(row[0])
                assert t.utcoffset() == timedelta(0) and t.minute == t.second == t.microsecond == 0
                assert previous is None or t > previous, (path, line, "unordered/duplicate")
                o, h, l, c, v = map(Decimal, row[1:])
                assert all(x.is_finite() for x in [o, h, l, c, v])
                assert min(o, h, l, c) > 0 and v >= 0 and l <= min(o, c) <= max(o, c) <= h
                if previous:
                    hours = int((t - previous).total_seconds() / 3600)
                    intervals[hours] += 1
                    if hours > 1:
                        gaps.append({"from": (previous + timedelta(hours=1)).isoformat(), "to": t.isoformat(),
                                     "absentHours": hours - 1, "classification": "UNVERIFIED: observation alone is not a calendar"})
                writer.writerow([t.strftime("%Y-%m-%d %H:%M"), *row[1:]])
                annual[t.year] += 1
                times.append(t)
                previous = t
        # Independent exported readback, assert EVERY numeric token unchanged.
        exported = list(csv.reader(target.read_text().splitlines()))[1:]
        assert len(exported) == len(rows)
        for a, b in zip(rows, exported):
            assert a[1:] == b[1:] and utc(a[0]) == datetime.strptime(b[0], "%Y-%m-%d %H:%M").replace(tzinfo=timezone.utc)
        target_hash = sha(target.read_bytes())
        manifest["files"].append({"file": target.name, "sha256": target_hash, "quoteBasis": "BID", "sessionGaps": []})
        save(out / (symbol + "-unverified-gaps.json"), gaps)
        coverage = {}
        for label, (warm, start, end) in PERIODS.items():
            w, s, e = [utc(x + "T00:00:00Z") for x in (warm, start, end)]
            selected = [t for t in times if w <= t < e]
            coverage[label] = {"warmupBars": sum(w <= t < s for t in times), "evaluationBars": sum(s <= t < e for t in times),
                               "first": selected[0].isoformat(), "lastOpen": selected[-1].isoformat(),
                               "unverifiedInteriorGaps": sum(utc(g["from"]) < e and utc(g["to"]) > w for g in gaps),
                               "completeAccountMetrics": None}
        audit.append({"symbol": symbol, "class": CLASSES[symbol], "source": str(path), "sourceSha256": expected,
                      "adaptedFile": str(target), "adaptedSha256": target_hash, "rows": len(rows), "first": times[0].isoformat(),
                      "lastOpen": times[-1].isoformat(), "annualRows": dict(sorted(annual.items())), "intervalHours": dict(sorted(intervals.items())),
                      "unverifiedGapEvents": len(gaps), "numericTokenReadback": "all 5 numeric columns exact for every row",
                      "volume": "provider best-price volume, not tick count or traded volume", "coverage": coverage})
    save(out / "manifest.json", manifest)
    save(out / "data-audit.json", audit)
    return audit


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--source", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--repo", type=Path, required=True)
    p.add_argument("--execute", action="store_true")
    args = p.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    prereg = args.repo / "reports/autonomy/research-preregistration.md"
    save(args.out / "binding.json", {"base": "351e4897480f11dce981cf664c4cfe503e2a05d7", "preregistrationSha256": sha(prereg.read_bytes()),
                                    "adapterSha256": sha(Path(__file__).read_bytes()), "gitHeadBeforeRuns": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=args.repo, text=True).strip()})
    audit = adapt(args.source, args.out)
    profile = json.loads((args.repo / "config/profile_default.json").read_text())
    profiles = args.out / "profiles"
    profiles.mkdir()
    plan = []
    dll = args.repo / "tools/Apex.Backtester/bin/Release/net8.0/Apex.Backtester.dll"
    for family in FAMILIES:
        for symbol in SOURCES:
            if family == "IndexDipBuy" and symbol != "US500":
                continue
            cls = CLASSES[symbol]
            entries = [e for e in profile["Entries"] if e["Strategy"] == family and e["AssetClass"] == cls]
            assert len(entries) == 1, (family, cls, "missing or duplicate actual default entry")
            entry = dict(entries[0], Enabled=True, Status="Experimental", Evidence="Offline frozen fixed parameter copy only; NEVER production activation")
            profile_path = profiles / f"{family}-{symbol}.json"
            save(profile_path, {"Version": "task4-offline-fixed-default", "Entries": [entry]})
            for label, (warmup, start, end) in PERIODS.items():
                name = f"fixed-{family}-{symbol}-{label}"
                cmd = ["dotnet", str(dll), "portfolio", "--data", str(args.out / "data"), "--manifest", str(args.out / "manifest.json"),
                       "--out", str(args.out / name), "--symbol", symbol, "--warmup-from", warmup, "--from", start, "--to", end,
                       "--cost", "base", "--intrabar", "h1", "--fx-max-age-hours", "2", "--profiles", str(profile_path)]
                plan.append({"id": name, "kind": "fixed", "family": family, "symbol": symbol, "class": cls, "period": label, "command": cmd})
            name = f"wf-{family}-{symbol}"
            cmd = ["dotnet", str(dll), "research", "--data", str(args.out / "data"), "--manifest", str(args.out / "manifest.json"),
                   "--out", str(args.out / name), "--symbol", symbol, "--warmup-from", "2012-01-01", "--from", "2014-01-01", "--to", "2024-01-01",
                   "--cost", "base", "--intrabar", "h1", "--fx-max-age-hours", "2", "--only", family, "--is-years", "4", "--max-combos", "2", "--max-runs", "160", "--parallel", "1", "--tag", "task4"]
            plan.append({"id": name, "kind": "wf", "family": family, "symbol": symbol, "class": cls, "period": "rolling-2014-2023", "command": cmd})
    assert len(plan) == 64
    # Freeze exact invocation list before invoking the CLI. No retries/loosening after outcomes.
    save(args.out / "attempt-plan.json", plan)
    results = []
    if args.execute:
        for run in plan:
            proc = subprocess.run(run["command"], cwd=args.repo, capture_output=True, text=True, timeout=240)
            run_dir = args.out / run["id"]
            run_dir.mkdir(exist_ok=True)
            (run_dir / "console.log").write_text(proc.stdout + proc.stderr)
            report_path = run_dir / "run.json"
            report = json.loads(report_path.read_text()) if report_path.exists() else {"success": False, "error": "NO run.json: process failure"}
            result = dict(run, exitCode=proc.returncode, success=report["success"], runJsonSha256=sha(report_path.read_bytes()) if report_path.exists() else None,
                          error=report.get("error"), metrics=None if not report["success"] else report["result"],
                          sourceDllHashes=report.get("source"))
            results.append(result)
            with (args.out / "attempts.jsonl").open("a") as log:
                log.write(json.dumps(result) + "\n")
            print(run["id"], proc.returncode, "SUCCESS" if report["success"] else str(report.get("error", "" )).splitlines()[0], flush=True)
        assert len(results) == 64
        save(args.out / "attempt-summary.json", {"attempts": len(results), "successful": sum(x["success"] for x in results),
                                                 "failed": sum(not x["success"] for x in results), "zeroTradeSuccessful": sum(x["success"] and x["metrics"].get("runs") == 0 for x in results),
                                                 "attemptIds": [x["id"] for x in results], "dataAudit": audit})


if __name__ == "__main__":
    main()
