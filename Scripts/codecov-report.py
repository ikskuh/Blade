#!/usr/bin/env python3
"""Print a short coverage summary from a Cobertura XML report."""

from __future__ import annotations

import sys
import xml.etree.ElementTree as et
from pathlib import Path


def main() -> int:
    report_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("coverage/coverage.cobertura.xml")

    root = et.parse(report_path).getroot()
    line_rate = float(root.get("line-rate", "0")) * 100
    branch_rate = float(root.get("branch-rate", "0")) * 100

    print(
        f"Line coverage:   {line_rate:.1f}% "
        f"({root.get('lines-covered')}/{root.get('lines-valid')})"
    )
    print(
        f"Branch coverage: {branch_rate:.1f}% "
        f"({root.get('branches-covered')}/{root.get('branches-valid')})"
    )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
