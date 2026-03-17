#!/usr/bin/env python3
"""Generate a markdown coverage report for changed files in a pull request."""

from __future__ import annotations

import argparse
import subprocess
import sys
import xml.etree.ElementTree as element_tree
from pathlib import Path
from typing import Dict


CoverageStats = Dict[str, Dict[str, int]]


def parse_coverage(path: Path) -> CoverageStats:
    tree = element_tree.parse(path)
    root = tree.getroot()
    stats: CoverageStats = {}

    for class_element in root.findall('.//class'):
        file_name = class_element.get('filename')
        if not file_name:
            continue

        entry = stats.setdefault(
            file_name,
            {
                'line_valid': 0,
                'line_covered': 0,
                'branch_valid': 0,
                'branch_covered': 0,
            },
        )

        for line in class_element.findall('./lines/line'):
            entry['line_valid'] += 1
            if int(line.get('hits', '0')) > 0:
                entry['line_covered'] += 1

            condition_coverage = line.get('condition-coverage')
            if condition_coverage and '(' in condition_coverage and '/' in condition_coverage:
                ratio = condition_coverage.split('(', 1)[1].split(')', 1)[0]
                covered_raw, valid_raw = ratio.split('/', 1)
                entry['branch_covered'] += int(covered_raw.strip())
                entry['branch_valid'] += int(valid_raw.strip())

    return stats


def percent(covered: int, valid: int) -> float:
    if valid == 0:
        return 100.0
    return (covered / valid) * 100.0


def delta_percentage(current: float, previous: float | None) -> str:
    if previous is None:
        return 'n/a'
    return f"{current - previous:+.1f}%"


def changed_files(base_ref: str) -> list[str]:
    diff_targets = [f'origin/{base_ref}...HEAD', f'{base_ref}...HEAD']
    for target in diff_targets:
        completed = subprocess.run(
            ['git', 'diff', '--name-only', target],
            check=False,
            capture_output=True,
            text=True,
        )
        if completed.returncode == 0:
            return [line.strip() for line in completed.stdout.splitlines() if line.strip()]

    return []


def build_report(pr_coverage: CoverageStats, base_coverage: CoverageStats, base_ref: str, base_available: bool) -> str:
    changed = changed_files(base_ref)
    covered_changed_files = [name for name in changed if name in pr_coverage]

    lines: list[str] = [
        '<!-- blade-coverage-comment -->',
        '## Coverage summary for changed source files',
        '',
    ]

    if not covered_changed_files:
        lines.append('No changed source files were found in the Cobertura report.')
    else:
        lines.extend(
            [
                '| File | Lines | Line Cov | Branch Cov | ΔLines | ΔLine Cov | ΔBranch Cov |',
                '| --- | ---: | ---: | ---: | ---: | ---: | ---: |',
            ]
        )

        total_line_valid = 0
        total_line_covered = 0
        total_branch_valid = 0
        total_branch_covered = 0

        total_base_line_valid = 0
        total_base_line_covered = 0
        total_base_branch_valid = 0
        total_base_branch_covered = 0

        for file_name in sorted(covered_changed_files):
            current = pr_coverage[file_name]
            previous = base_coverage.get(file_name)

            current_line_cov = percent(current['line_covered'], current['line_valid'])
            current_branch_cov = percent(current['branch_covered'], current['branch_valid'])

            previous_line_cov: float | None = None
            previous_branch_cov: float | None = None
            delta_lines = 'n/a'

            if previous is not None:
                previous_line_cov = percent(previous['line_covered'], previous['line_valid'])
                previous_branch_cov = percent(previous['branch_covered'], previous['branch_valid'])
                delta_lines = f"{current['line_valid'] - previous['line_valid']:+d}"

                total_base_line_valid += previous['line_valid']
                total_base_line_covered += previous['line_covered']
                total_base_branch_valid += previous['branch_valid']
                total_base_branch_covered += previous['branch_covered']

            total_line_valid += current['line_valid']
            total_line_covered += current['line_covered']
            total_branch_valid += current['branch_valid']
            total_branch_covered += current['branch_covered']

            lines.append(
                '| {file} | {line_valid} | {line_cov:.1f}% | {branch_cov:.1f}% | {delta_lines} | {delta_line_cov} | {delta_branch_cov} |'.format(
                    file=file_name,
                    line_valid=current['line_valid'],
                    line_cov=current_line_cov,
                    branch_cov=current_branch_cov,
                    delta_lines=delta_lines,
                    delta_line_cov=delta_percentage(current_line_cov, previous_line_cov if base_available else None),
                    delta_branch_cov=delta_percentage(current_branch_cov, previous_branch_cov if base_available else None),
                )
            )

        total_line_cov = percent(total_line_covered, total_line_valid)
        total_branch_cov = percent(total_branch_covered, total_branch_valid)

        total_delta_lines = 'n/a'
        total_delta_line_cov = 'n/a'
        total_delta_branch_cov = 'n/a'

        if base_available:
            total_delta_lines = f"{total_line_valid - total_base_line_valid:+d}"
            total_delta_line_cov = delta_percentage(total_line_cov, percent(total_base_line_covered, total_base_line_valid))
            total_delta_branch_cov = delta_percentage(total_branch_cov, percent(total_base_branch_covered, total_base_branch_valid))

        lines.extend(
            [
                '',
                '| Total (changed files) | {line_valid} | {line_cov:.1f}% | {branch_cov:.1f}% | {delta_lines} | {delta_line_cov} | {delta_branch_cov} |'.format(
                    line_valid=total_line_valid,
                    line_cov=total_line_cov,
                    branch_cov=total_branch_cov,
                    delta_lines=total_delta_lines,
                    delta_line_cov=total_delta_line_cov,
                    delta_branch_cov=total_delta_branch_cov,
                ),
            ]
        )

    lines.append('')
    if base_available:
        lines.append(f'Deltas are computed against `origin/{base_ref}` coverage from this workflow run.')
    else:
        lines.append(f'Deltas are unavailable because coverage could not be generated for `origin/{base_ref}` in this run.')

    return '\n'.join(lines)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description='Generate a PR coverage markdown report.')
    parser.add_argument('--pr-coverage', required=True, type=Path)
    parser.add_argument('--base-coverage', required=True, type=Path)
    parser.add_argument('--report', required=True, type=Path)
    parser.add_argument('--base-ref', required=True)
    parser.add_argument('--base-available', required=True, choices=['true', 'false'])
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)

    pr_cov = parse_coverage(args.pr_coverage)
    base_available = args.base_available == 'true'
    base_cov: CoverageStats = {}
    if base_available and args.base_coverage.exists():
        base_cov = parse_coverage(args.base_coverage)

    report_text = build_report(pr_cov, base_cov, args.base_ref, base_available)
    args.report.write_text(report_text, encoding='utf-8')
    return 0


if __name__ == '__main__':
    raise SystemExit(main(sys.argv[1:]))
