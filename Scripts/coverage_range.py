#!/usr/bin/env python3
"""Query uncovered lines in a coverage report for a specific class and line range.

Usage:
    python Scripts/coverage_range.py <class_pattern> <start_line> <end_line> [coverage_file]

Examples:
    python Scripts/coverage_range.py Binder 652 740
    python Scripts/coverage_range.py MirLowerer 620 770 coverage/coverage.cobertura.xml
"""

import sys
import xml.etree.ElementTree as ET


def main():
    if len(sys.argv) < 4:
        print(__doc__.strip())
        sys.exit(1)

    class_pattern = sys.argv[1]
    start_line = int(sys.argv[2])
    end_line = int(sys.argv[3])
    coverage_file = sys.argv[4] if len(sys.argv) > 4 else "coverage/coverage.cobertura.xml"

    tree = ET.parse(coverage_file)
    root = tree.getroot()
    found = False

    for cls in root.findall(".//class"):
        name = cls.get("name", "")
        if class_pattern not in name:
            continue
        for line in cls.findall(".//line"):
            num = int(line.get("number"))
            hits = int(line.get("hits"))
            if start_line <= num <= end_line and hits == 0:
                cond = line.get("condition-coverage", "")
                suffix = f"  ({cond})" if cond else ""
                print(f"  {name} L{num}: 0 hits{suffix}")
                found = True

    if not found:
        print("All lines in range are covered.")


if __name__ == "__main__":
    main()
