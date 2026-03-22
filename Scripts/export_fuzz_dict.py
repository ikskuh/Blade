# /usr/bin/env python


from argparse import ArgumentParser
from pathlib import Path


def main():
    parser = ArgumentParser()
    parser.add_argument("--from", type=Path, dest="_from")
    parser.add_argument("--dict", type=Path)

    args = parser.parse_args()

    from_dir: Path = args._from
    dict_path: Path = args.dict

    for path in from_dir.rglob("*.blade"):
        
        print(path)
        path.read_text('utf-8').split('\b');
    
        break


if __name__ == "__main__":
    main()
