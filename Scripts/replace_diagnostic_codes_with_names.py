#!/usr/bin/env python3
"""Replace diagnostic code tokens in .blade files with diagnostic names."""

from __future__ import annotations

import re
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
MESSAGES_DEF = REPOSITORY_ROOT / "Blade" / "Diagnostics" / "Messages.def"
DIAGNOSTIC_HEADER_RE = re.compile(
    r"^\s*(?:located|generic)\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*([EWI]\d{4})\b"
)
DIAGNOSTIC_CODE_RE = re.compile(r"\b[EWI]\d{4}\b")


def load_code_names() -> dict[str, str]:
    code_names: dict[str, str] = {}
    for line in MESSAGES_DEF.read_text(encoding="utf-8").splitlines():
        match = DIAGNOSTIC_HEADER_RE.match(line)
        if match is None:
            continue

        name = match.group(1)
        code = match.group(2)
        code_names[code] = name

    return code_names


def replace_codes(path: Path, code_names: dict[str, str]) -> bool:
    original_text = path.read_text(encoding="utf-8")

    def replace(match: re.Match[str]) -> str:
        code = match.group(0)
        return code_names.get(code, code)

    updated_text = DIAGNOSTIC_CODE_RE.sub(replace, original_text)
    if updated_text == original_text:
        return False

    path.write_text(updated_text, encoding="utf-8")
    return True


def iter_blade_files() -> list[Path]:
    return sorted(
        path
        for path in REPOSITORY_ROOT.rglob("*.blade")
        if path.is_file()
        and not any(
            part in {".git", ".artifacts", "bin", "obj"}
            for part in path.relative_to(REPOSITORY_ROOT).parts
        )
    )


def main() -> int:
    code_names = load_code_names()
    if not code_names:
        raise RuntimeError(f"No diagnostics found in {MESSAGES_DEF}")

    changed_paths: list[Path] = []
    for path in iter_blade_files():
        if replace_codes(path, code_names):
            changed_paths.append(path)

    for path in changed_paths:
        print(path.relative_to(REPOSITORY_ROOT))

    print(f"Updated {len(changed_paths)} .blade file(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
