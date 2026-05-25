#!/usr/bin/env python3
"""Compute union line/branch coverage from cobertura reports and fail if line coverage
is below the requested threshold. Run after `dotnet test ... --collect:"XPlat Code Coverage"`.

Usage:
  python3 scripts/check-coverage.py --threshold 80 --results-dir TestResults
"""
import argparse
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--threshold", type=float, default=80.0, help="Minimum line-coverage percent.")
    ap.add_argument("--results-dir", type=Path, default=Path("TestResults"))
    args = ap.parse_args()

    covered_lines: dict[str, set[int]] = defaultdict(set)
    valid_lines: dict[str, set[int]] = defaultdict(set)
    covered_branches: dict[str, set[tuple[int, int]]] = defaultdict(set)
    valid_branches: dict[str, set[tuple[int, int]]] = defaultdict(set)

    reports = list(args.results_dir.rglob("coverage.cobertura.xml"))
    if not reports:
        print(f"No coverage.cobertura.xml found under {args.results_dir}", file=sys.stderr)
        return 1
    for cobertura in reports:
        tree = ET.parse(cobertura)
        for cls in tree.iter("class"):
            fname = cls.get("filename", "")
            for line in cls.iter("line"):
                ln = int(line.get("number", "0"))
                hits = int(line.get("hits", "0"))
                valid_lines[fname].add(ln)
                if hits > 0:
                    covered_lines[fname].add(ln)
                if line.get("branch", "false").lower() == "true":
                    cc = line.get("condition-coverage", "")
                    if "(" in cc and "/" in cc:
                        seg = cc[cc.index("(") + 1:cc.index(")")]
                        try:
                            cov, tot = (int(x) for x in seg.split("/"))
                        except ValueError:
                            continue
                        for i in range(tot):
                            valid_branches[fname].add((ln, i))
                        for i in range(cov):
                            covered_branches[fname].add((ln, i))

    total_valid = sum(len(s) for s in valid_lines.values())
    total_covered = sum(len(covered_lines[f] & valid_lines[f]) for f in valid_lines)
    bvalid = sum(len(s) for s in valid_branches.values())
    bcov = sum(len(covered_branches[f] & valid_branches[f]) for f in valid_branches)
    line_pct = (100 * total_covered / total_valid) if total_valid else 0.0
    branch_pct = (100 * bcov / bvalid) if bvalid else 0.0

    print(f"Reports merged: {len(reports)}")
    print(f"Line coverage: {total_covered}/{total_valid} = {line_pct:.2f}%")
    print(f"Branch coverage: {bcov}/{bvalid} = {branch_pct:.2f}%")
    print(f"Threshold: {args.threshold:.2f}% (line)")

    if line_pct + 1e-9 < args.threshold:
        print(f"FAIL: line coverage {line_pct:.2f}% is below threshold {args.threshold:.2f}%", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
