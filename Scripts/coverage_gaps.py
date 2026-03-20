#!/usr/bin/env python3
"""Analyze coverage gaps from cobertura XML. Shows uncovered lines per class."""

import xml.etree.ElementTree as ET
import sys
from collections import defaultdict

def main():
    xml_path = sys.argv[1] if len(sys.argv) > 1 else "coverage/coverage.cobertura.xml"
    min_uncovered = int(sys.argv[2]) if len(sys.argv) > 2 else 1

    tree = ET.parse(xml_path)
    root = tree.getroot()

    gaps = []

    for package in root.findall(".//package"):
        for cls in package.findall(".//class"):
            name = cls.get("name")
            filename = cls.get("filename")
            uncovered_lines = []
            uncovered_branches = []

            for line in cls.findall(".//line"):
                hits = int(line.get("hits", 0))
                line_num = int(line.get("number"))
                if hits == 0:
                    uncovered_lines.append(line_num)
                # Check branch coverage
                cb = line.get("condition-coverage")
                if cb and "100%" not in cb:
                    uncovered_branches.append((line_num, cb))

            if len(uncovered_lines) >= min_uncovered or uncovered_branches:
                gaps.append((name, filename, uncovered_lines, uncovered_branches))

    gaps.sort(key=lambda x: len(x[2]), reverse=True)

    for name, filename, lines, branches in gaps:
        if not lines and not branches:
            continue
        print(f"\n=== {name} ({filename}) ===")
        if lines:
            # Group consecutive lines into ranges
            ranges = []
            start = lines[0]
            end = lines[0]
            for l in lines[1:]:
                if l == end + 1:
                    end = l
                else:
                    ranges.append((start, end))
                    start = l
                    end = l
            ranges.append((start, end))
            range_strs = [f"{s}-{e}" if s != e else str(s) for s, e in ranges]
            print(f"  Uncovered lines ({len(lines)}): {', '.join(range_strs)}")
        if branches:
            for line_num, cb in branches:
                print(f"  Partial branch at line {line_num}: {cb}")

if __name__ == "__main__":
    main()
