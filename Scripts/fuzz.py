#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path)
    parser.add_argument("--corpus", type=Path)
    parser.add_argument("--findings", type=Path)
    parser.add_argument("--build", type=Path)
    parser.add_argument("--x", default=None)
    parser.add_argument("--t", type=int, default=10000)
    parser.add_argument("--command", default="sharpfuzz")
    return parser.parse_args()


def run_command(args: list[str], env: dict[str, str] | None = None) -> int:
    completed = subprocess.run(args, env=env, check=False)
    return completed.returncode


def main() -> int:
    args = parse_args()

    output_dir = args.build
    findings_dir = args.findings

    if not output_dir.exists():
        sys.stderr.write(f"{output_dir.as_posix()!r} must exist\n")
        return 1

    if not findings_dir.exists():
        sys.stderr.write(f"{findings_dir.as_posix()!r} must exist\n")
        return 1

    publish_exit_code = run_command(
        [
            "dotnet",
            "publish",
            args.project,
            "-c",
            "release",
            "-o",
            str(output_dir),
        ]
    )
    if publish_exit_code != 0:
        return publish_exit_code

    project_name = Path(args.project).stem
    project_dll = f"{project_name}.dll"
    project = output_dir / project_dll

    exclusions = {
        "dnlib.dll",
        "SharpFuzz.dll",
        "SharpFuzz.Common.dll",
        project_dll,
    }

    fuzzing_targets = [
        path
        for path in output_dir.glob("*.dll")
        if path.name not in exclusions and not path.name.startswith("System.")
    ]

    if not fuzzing_targets:
        print("No fuzzing targets found", file=sys.stderr)
        return 1

    for fuzzing_target in fuzzing_targets:
        print(f"Instrumenting {fuzzing_target}")
        instrument_exit_code = run_command([args.command, str(fuzzing_target)])
        if instrument_exit_code != 0:
            print(
                f"An error occurred while instrumenting {fuzzing_target}",
                file=sys.stderr,
            )
            return 1

    environment = os.environ.copy()
    environment["AFL_SKIP_BIN_CHECK"] = "1"

    afl_command = [
        "afl-fuzz",
        "-i",
        args.corpus,
        "-o",
        str(findings_dir),
        "-t",
        str(args.t),
        "-m",
        "none",
    ]
    if args.x is not None:
        afl_command.extend(["-x", args.x])

    afl_command.extend(["dotnet", str(project)])
    return run_command(afl_command, env=environment)


if __name__ == "__main__":
    sys.exit(main())
