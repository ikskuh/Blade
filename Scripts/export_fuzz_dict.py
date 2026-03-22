# /usr/bin/env python

import re

from argparse import ArgumentParser
from pathlib import Path


ELEMENT_PATTERN = re.compile(r"\w+|\S+")


def stripcomment(line: str) -> str:
    index = line.find("//")
    if index >= 0:
        return line[0:index].rstrip()
    return line


def main():
    parser = ArgumentParser()
    parser.add_argument("--from", type=Path, dest="_from")
    parser.add_argument("--dict", type=Path)

    args = parser.parse_args()

    from_dir: Path = args._from
    dict_path: Path = args.dict

    components: set[str] = {"(", ")", "{", "}", "[", "]", "->", ".", ";"}

    for path in from_dir.rglob("*.blade"):
        source = "\n".join(
            stripped
            for line in path.read_text("utf-8").splitlines()
            if len(stripped := stripcomment(line)) > 0
        )

        components.update(ELEMENT_PATTERN.findall(source))

    dict_path.write_text(
        "\n".join(f'"{part.replace('"', '\\"')}"' for part in sorted(components))
    )

if __name__ == "__main__":
    main()
