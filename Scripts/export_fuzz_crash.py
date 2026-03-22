# /usr/bin/env python

from argparse import ArgumentParser
from pathlib import Path

def main():

    parser = ArgumentParser()
    parser.add_argument("--count", type=int)
    parser.add_argument("--from", type=Path, dest="_from")
    parser.add_argument("--to", type=Path)

    args = parser.parse_args()

    count: int = args.count
    from_dir: Path = args._from
    to_dir: Path = args.to

    start_count = len(tuple(to_dir.glob("*.blade.crash")))

    for _, src_file in zip(range(count), from_dir.glob("id:*")):
        while True:
            start_count += 1
            try:
                dest = to_dir / f"issue-{start_count:05d}.blade.crash"
                if dest.exists():
                    raise FileExistsError
                src_file.move(dest)
                print(f"Moved {src_file} to {dest}")
                break
            except FileExistsError:
                pass


if __name__ == "__main__":
    main()
