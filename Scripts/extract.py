#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
from dataclasses import asdict, dataclass
from pathlib import Path

import pandas as pd


SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
DEFAULT_WORKBOOK = REPO_ROOT / "Docs" / "propeller2-instructions.xlsx"
DEFAULT_OUTPUT = REPO_ROOT / "Data" / "P2InstructionMetadata.json"


NO_REGISTER_EFFECT_MNEMONICS = frozenset({"NOP", "REP", "AUGS", "AUGD"})
READWRITE_DESTINATION_MNEMONICS = frozenset({"BITC", "BITNC", "BITNZ", "BITZ"})
PURE_REGISTER_LOCAL_MNEMONICS = frozenset(
    {
        "MOV",
        "NEG",
        "ABS",
        "NOT",
        "ADD",
        "SUB",
        "AND",
        "OR",
        "XOR",
        "SHL",
        "SHR",
        "SAR",
        "ROL",
        "ROR",
        "ENCOD",
        "DECOD",
        "BMASK",
        "ZEROX",
        "SIGNX",
        "BITH",
        "BITL",
        "BITNOT",
        "BITZ",
        "BITNZ",
        "BITRND",
    }
)
SPECIAL_REGISTER_INFO = {
    "IJMP3": {"address": 0x1F0, "description": "INT3 call address."},
    "IRET3": {"address": 0x1F1, "description": "INT3 return address."},
    "IJMP2": {"address": 0x1F2, "description": "INT2 call address."},
    "IRET2": {"address": 0x1F3, "description": "INT2 return address."},
    "IJMP1": {"address": 0x1F4, "description": "INT1 call address."},
    "IRET1": {"address": 0x1F5, "description": "INT1 return address."},
    "PA": {"address": 0x1F6, "description": "Used with CALLPA, CALLD and LOC."},
    "PB": {"address": 0x1F7, "description": "Used with CALLPB, CALLD and LOC."},
    "PTRA": {"address": 0x1F8, "description": "Pointer A register."},
    "PTRB": {"address": 0x1F9, "description": "Pointer B register."},
    "DIRA": {"address": 0x1FA, "description": "I/O port A direction register."},
    "DIRB": {"address": 0x1FB, "description": "I/O port B direction register."},
    "OUTA": {"address": 0x1FC, "description": "I/O port A output register."},
    "OUTB": {"address": 0x1FD, "description": "I/O port B output register."},
    "INA": {"address": 0x1FE, "description": "I/O port A input register."},
    "INB": {"address": 0x1FF, "description": "I/O port B input register."},
}
WRITTEN_REGISTER_ORDER = ("D", *SPECIAL_REGISTER_INFO.keys())
FLAG_EFFECT_ORDER = (
    "none",
    "WC",
    "WZ",
    "WCZ",
    "ANDC",
    "ANDZ",
    "ORC",
    "ORZ",
    "XORC",
    "XORZ",
)
FLAG_EFFECT_INFO = {
    "none": {"targetFlag": "none", "operator": "none"},
    "WC": {"targetFlag": "c", "operator": "set"},
    "WZ": {"targetFlag": "z", "operator": "set"},
    "WCZ": {"targetFlag": "both", "operator": "set"},
    "ANDC": {"targetFlag": "c", "operator": "and"},
    "ANDZ": {"targetFlag": "z", "operator": "and"},
    "ORC": {"targetFlag": "c", "operator": "or"},
    "ORZ": {"targetFlag": "z", "operator": "or"},
    "XORC": {"targetFlag": "c", "operator": "xor"},
    "XORZ": {"targetFlag": "z", "operator": "xor"},
}


@dataclass(frozen=True)
class SheetRow:
    mnemonic: str
    operand_count: int
    operand_text: str
    encoding: str
    allowed_flag_effects: tuple[str, ...]
    group: str
    description: str
    register_write: str
    stack_rw: str
    is_alias: bool
    source_sheet: str


@dataclass(frozen=True)
class OperandModel:
    role: str
    type: str
    bitWidth: int
    access: str
    supportsImmediate: str
    augPrefix: str


@dataclass(frozen=True)
class ClassificationModel:
    isCall: bool
    isJump: bool
    isBranch: bool
    isReturn: bool
    hasNoRegisterEffect: bool
    isPureRegisterLocal: bool


@dataclass(frozen=True)
class InstructionFormModel:
    isAlias: bool
    summary: str | None
    allowedFlagEffects: list[str]
    operands: list[OperandModel]
    writtenRegisters: list[str]
    hwStackEffect: str
    classification: ClassificationModel


@dataclass(frozen=True)
class MnemonicModel:
    instructionForms: list[InstructionFormModel]


@dataclass(frozen=True)
class ExportModel:
    conditionCodes: dict[str, dict[str, str | bool | None]]
    modczOperands: dict[str, dict[str, str | bool | None]]
    specialRegisters: dict[str, dict[str, int | str]]
    flagEffects: dict[str, dict[str, str]]
    mnemonics: dict[str, MnemonicModel]


def normalize_text(value: object) -> str:
    if pd.isna(value):
        return ""

    if value is None:
        return ""

    text = str(value).replace("\xa0", " ")
    text = text.replace("…", "...")
    return re.sub(r"\s+", " ", text).strip()


def load_sheet_rows(workbook_path: Path, sheet_name: str) -> list[SheetRow]:
    frame = pd.read_excel(workbook_path, sheet_name=sheet_name)
    rows: list[SheetRow] = []

    syntax_column = frame.columns[1]
    group_column = frame.columns[2]
    encoding_column = frame.columns[3]
    alias_column = frame.columns[4]
    description_column = frame.columns[5]
    register_write_column = frame.columns[11]
    stack_rw_column = frame.columns[13]

    for _, row in frame.iterrows():
        syntax = normalize_text(row[syntax_column])
        if not syntax:
            continue

        mnemonic, operand_text, allowed_flag_effects = parse_assembly_syntax(syntax)
        if mnemonic == "<EMPTY>":
            continue

        rows.append(
            SheetRow(
                mnemonic=mnemonic,
                operand_count=count_operands(operand_text),
                operand_text=operand_text,
                encoding=normalize_text(row[encoding_column]),
                allowed_flag_effects=allowed_flag_effects,
                group=normalize_text(row[group_column]),
                description=normalize_text(row[description_column]),
                register_write=normalize_text(row[register_write_column]),
                stack_rw=normalize_text(row[stack_rw_column]),
                is_alias=normalize_text(row[alias_column]).lower() == "alias",
                source_sheet=sheet_name,
            )
        )

    return rows


def parse_assembly_syntax(syntax: str) -> tuple[str, str, tuple[str, ...]]:
    parts = syntax.split(" ", 1)
    mnemonic = parts[0].upper()
    remainder = parts[1].strip() if len(parts) > 1 else ""

    match = re.search(r"(?:\{([^}]+)\}|([A-Z]+(?:/[A-Z]+)+))$", remainder)
    allowed_flag_effects: tuple[str, ...] = ()
    if match is not None:
        token_text = match.group(1) or match.group(2) or ""
        tokens = tuple(part.strip().upper() for part in token_text.split("/") if part.strip())
        if tokens and all(token in FLAG_EFFECT_ORDER for token in tokens):
            if match.group(1) is not None:
                allowed_flag_effects = ("none", *tokens)
            else:
                allowed_flag_effects = tokens
            remainder = remainder[: match.start()].rstrip()

    return mnemonic, remainder, allowed_flag_effects


def count_operands(operand_text: str) -> int:
    if not operand_text:
        return 0
    return operand_text.count(",") + 1


def load_condition_codes(workbook_path: Path) -> dict[str, dict[str, str | bool | None]]:
    return load_named_alias_entries(
        workbook_path=workbook_path,
        sheet_name="Prefixes",
        parse_name=parse_prefix_name,
    )


def load_modcz_operands(workbook_path: Path) -> dict[str, dict[str, str | bool | None]]:
    return load_named_alias_entries(
        workbook_path=workbook_path,
        sheet_name="MODCZ",
        parse_name=lambda syntax: normalize_text(syntax).upper(),
    )


def load_named_alias_entries(
    workbook_path: Path,
    sheet_name: str,
    parse_name,
) -> dict[str, dict[str, str | bool | None]]:
    frame = pd.read_excel(workbook_path, sheet_name=sheet_name)
    syntax_column = frame.columns[1]
    encoding_column = frame.columns[3]
    alias_column = frame.columns[4]

    rows: list[tuple[str, str, bool]] = []
    for _, row in frame.iterrows():
        syntax = normalize_text(row[syntax_column])
        if not syntax:
            continue

        name = parse_name(syntax)
        if not name:
            continue

        rows.append(
            (
                name,
                normalize_text(row[encoding_column]),
                normalize_text(row[alias_column]).lower() == "alias",
            )
        )

    canonical_by_encoding: dict[str, str] = {}
    for name, encoding, is_alias in rows:
        if is_alias:
            continue
        existing = canonical_by_encoding.get(encoding)
        if existing is not None and existing != name:
            raise ValueError(f"Conflicting canonical names in {sheet_name}: {existing} vs {name}")
        canonical_by_encoding[encoding] = name

    result: dict[str, dict[str, str | bool | None]] = {}
    for name, encoding, is_alias in rows:
        if name in result:
            raise ValueError(f"Duplicate {sheet_name} key: {name}")

        canonical_name = canonical_by_encoding.get(encoding)
        if is_alias and canonical_name is None:
            raise ValueError(f"Alias {name} in {sheet_name} has no canonical entry")

        result[name] = {
            "isAlias": is_alias,
            "canonicalName": canonical_name if is_alias else None,
        }

    return result


def parse_prefix_name(syntax: str) -> str:
    return normalize_text(syntax).split(" ", 1)[0].upper()


def aggregate_instruction_forms(rows: list[SheetRow]) -> dict[str, MnemonicModel]:
    grouped_forms: dict[tuple[str, str, str], list[SheetRow]] = {}
    for row in rows:
        grouped_forms.setdefault((row.mnemonic, row.operand_text, row.group), []).append(row)

    forms_by_mnemonic: dict[str, list[InstructionFormModel]] = {}
    for (mnemonic, _, _), group_rows in sorted(grouped_forms.items()):
        forms_by_mnemonic.setdefault(mnemonic, []).append(build_instruction_form(group_rows))

    result: dict[str, MnemonicModel] = {}
    for mnemonic in sorted(forms_by_mnemonic):
        result[mnemonic] = MnemonicModel(
            instructionForms=forms_by_mnemonic[mnemonic],
        )

    return result


def build_instruction_form(group_rows: list[SheetRow]) -> InstructionFormModel:
    representative = group_rows[0]
    classification = classify_instruction(representative.group, representative.mnemonic)
    operand_sets = [build_operand_models(row, classification) for row in group_rows]
    operands = merge_operand_layouts(operand_sets)

    descriptions = {row.description for row in group_rows if row.description}
    summary = representative.description if len(descriptions) == 1 else None
    allowed_flag_effects = sorted(
        {effect for row in group_rows for effect in row.allowed_flag_effects},
        key=lambda effect: FLAG_EFFECT_ORDER.index(effect),
    )

    written_registers = merge_written_registers(parse_written_registers(row.register_write) for row in group_rows)
    hw_stack_effect = merge_stack_effects(parse_hw_stack_effect(row.stack_rw) for row in group_rows)

    return InstructionFormModel(
        isAlias=representative.is_alias,
        summary=summary,
        allowedFlagEffects=allowed_flag_effects,
        operands=operands,
        writtenRegisters=written_registers,
        hwStackEffect=hw_stack_effect,
        classification=classification,
    )


def build_operand_models(row: SheetRow, classification: ClassificationModel) -> list[OperandModel]:
    operands = split_operands(row.operand_text)
    d_access = infer_d_operand_access(row, operands, classification)
    models: list[OperandModel] = []

    for token in operands:
        role = infer_operand_role(token)
        supports_immediate = infer_supports_immediate(token)
        models.append(
            OperandModel(
                role=role,
                type=infer_operand_type(row, token, role, classification),
                bitWidth=infer_operand_bit_width(token, row.encoding, role),
                access=infer_operand_access(role, d_access),
                supportsImmediate=supports_immediate,
                augPrefix=infer_aug_prefix(role, supports_immediate),
            )
        )

    return models


def split_operands(operand_text: str) -> tuple[str, ...]:
    if not operand_text:
        return ()
    return tuple(part.strip() for part in operand_text.split(","))


def classify_instruction(group_name: str, mnemonic: str) -> ClassificationModel:
    upper_group_name = group_name.upper()
    is_call = "CALL" in upper_group_name
    is_return = "RETURN" in upper_group_name
    is_jump = upper_group_name in {"BRANCH A - JUMP", "BRANCH D - JUMP", "BRANCH D - SKIP", "BRANCH D - JUMP+SKIP"}
    is_branch = upper_group_name in {"BRANCH S - MOD & TEST", "BRANCH S - TEST", "EVENTS - BRANCH"}

    return ClassificationModel(
        isCall=is_call,
        isJump=is_jump,
        isBranch=is_branch,
        isReturn=is_return,
        hasNoRegisterEffect=mnemonic in NO_REGISTER_EFFECT_MNEMONICS,
        isPureRegisterLocal=mnemonic in PURE_REGISTER_LOCAL_MNEMONICS,
    )


def infer_d_operand_access(
    row: SheetRow,
    operands: tuple[str, ...],
    classification: ClassificationModel,
) -> str:
    if not any(infer_operand_role(token) == "D" for token in operands):
        return "None"

    writes_destination = "D" in parse_written_registers(row.register_write)

    if row.mnemonic == "LOC":
        return "Write"

    if classification.isReturn:
        return "None"

    if classification.isCall:
        return "Write" if writes_destination else "Read"

    if classification.isBranch:
        return "ReadWrite" if writes_destination else "Read"

    if row.mnemonic in READWRITE_DESTINATION_MNEMONICS:
        return "ReadWrite"

    if not writes_destination:
        return "Read"

    if reads_existing_destination(row.description):
        return "ReadWrite"

    return "Write"


def reads_existing_destination(description: str) -> bool:
    if not description:
        return False

    if "D =" in description:
        _, rhs = description.split("D =", 1)
        return re.search(r"\bD\b", rhs) is not None

    return (
        "write to D" not in description
        and "written with" not in description
        and re.search(r"\bD\b", description) is not None
        and description.startswith(("Add", "Subtract", "Increment", "Decrement", "Force", "Sum", "Mux", "Move bytes"))
    )


def infer_operand_role(token: str) -> str:
    normalized = token.upper().replace("{", "").replace("}", "").replace("\\", "")
    if normalized in {"C", "Z"}:
        return "MODCZ"

    if normalized == "PA/PB/PTRA/PTRB":
        return "address register"

    if normalized == "#A" or normalized == "A":
        return "ADDR"

    if re.fullmatch(r"#?N", normalized):
        return "N"

    if re.search(r"(^|[^A-Z])S(?:/P)?($|[^A-Z])", normalized):
        return "S"

    if re.search(r"(^|[^A-Z])D($|[^A-Z])", normalized):
        return "D"

    raise ValueError(f"Unsupported operand role token: {token}")


def infer_supports_immediate(token: str) -> str:
    if "{#}" in token:
        return "optional"

    if "#" in token:
        return "required"

    return "no"


def infer_operand_type(
    row: SheetRow,
    token: str,
    role: str,
    classification: ClassificationModel,
) -> str:
    upper_group = row.group.upper()
    upper_desc = row.description.upper()
    upper_token = token.upper()

    if role == "MODCZ":
        return "modcz"

    if "/P" in upper_token:
        if "LOOKUP TABLE" in upper_group:
            return "lut ptrexpr"
        if "HUB RAM" in upper_group:
            return "hub ptrexpr"

    if is_branch_target_operand(row.group, role, classification):
        return "branch target"

    if role in {"D", "S"}:
        if f"{role}[10:6]" in upper_desc and f"{role}[5:0]" in upper_desc:
            return "pinrange"

        if f"{role}[5:0]" in upper_desc and "PIN " in upper_desc:
            return "pin"

        if f"{role}[9:5]" in upper_desc and f"{role}[4:0]" in upper_desc and "BITS " in upper_desc:
            return "bitrange"

        if (
            f"BIT {role}[4:0]" in upper_desc
            or f"ABOVE BIT {role}[4:0]" in upper_desc
            or f"FROM BIT {role}[4:0]" in upper_desc
        ):
            return "bit"

    return "regular"


def is_branch_target_operand(group_name: str, role: str, classification: ClassificationModel) -> bool:
    upper_group = group_name.upper()

    if upper_group in {"BRANCH S - MOD & TEST", "BRANCH S - TEST", "EVENTS - BRANCH", "BRANCH S - CALL"}:
        return role == "S"

    if upper_group in {"BRANCH D - JUMP", "BRANCH D - SKIP", "BRANCH D - JUMP+SKIP", "BRANCH D - CALL", "BRANCH D - CALL+SKIP"}:
        return role == "D"

    if upper_group in {"BRANCH A - JUMP", "BRANCH A - CALL"}:
        return role == "ADDR"

    return classification.isBranch and role in {"D", "S", "ADDR"}


def infer_operand_bit_width(token: str, encoding: str, role: str) -> int:
    if role == "D":
        return count_encoding_bits(encoding, "D")

    if role == "S":
        return count_encoding_bits(encoding, "S")

    if role == "N":
        return count_encoding_bits(encoding, "N")

    if role == "MODCZ":
        return 4

    if role == "ADDR":
        return count_encoding_bits(encoding, "A")

    upper_token = token.upper()
    if "PA/PB/PTRA/PTRB" in upper_token:
        return count_encoding_bits(encoding, "W")

    return 0


def count_encoding_bits(encoding: str, symbol: str) -> int:
    return encoding.upper().count(symbol.upper())


def infer_operand_access(role: str, d_access: str) -> str:
    if role == "D":
        return d_access

    if role in {"S", "MODCZ", "ADDR"}:
        return "Read"

    return "None"


def infer_aug_prefix(role: str, supports_immediate: str) -> str:
    if supports_immediate == "no":
        return "None"

    if role == "D":
        return "AUGD"

    if role == "S":
        return "AUGS"

    return "None"


def merge_operand_layouts(layouts: list[list[OperandModel]]) -> list[OperandModel]:
    if not layouts:
        return []

    operand_count = len(layouts[0])
    if any(len(layout) != operand_count for layout in layouts):
        raise ValueError("Cannot merge forms with differing operand counts")

    merged: list[OperandModel] = []
    for operand_index in range(operand_count):
        candidates = [layout[operand_index] for layout in layouts]
        first = candidates[0]

        if any(candidate.role != first.role for candidate in candidates):
            raise ValueError(f"Conflicting operand roles at position {operand_index}")

        if any(candidate.type != first.type for candidate in candidates):
            raise ValueError(f"Conflicting operand types at position {operand_index}")

        if any(candidate.bitWidth != first.bitWidth for candidate in candidates):
            raise ValueError(f"Conflicting operand widths at position {operand_index}")

        if any(candidate.supportsImmediate != first.supportsImmediate for candidate in candidates):
            raise ValueError(f"Conflicting immediate support at position {operand_index}")

        if any(candidate.augPrefix != first.augPrefix for candidate in candidates):
            raise ValueError(f"Conflicting AUG prefix at position {operand_index}")

        access = candidates[0].access
        for candidate in candidates[1:]:
            access = merge_operand_access(access, candidate.access)

        merged.append(
            OperandModel(
                role=first.role,
                type=first.type,
                bitWidth=first.bitWidth,
                access=access,
                supportsImmediate=first.supportsImmediate,
                augPrefix=first.augPrefix,
            )
        )

    return merged


def merge_operand_access(left: str, right: str) -> str:
    left_reads = left in ("Read", "ReadWrite")
    left_writes = left in ("Write", "ReadWrite")
    right_reads = right in ("Read", "ReadWrite")
    right_writes = right in ("Write", "ReadWrite")

    reads = left_reads or right_reads
    writes = left_writes or right_writes

    if reads and writes:
        return "ReadWrite"

    if reads:
        return "Read"

    if writes:
        return "Write"

    return "None"


def parse_written_registers(register_write: str) -> tuple[str, ...]:
    normalized = register_write.upper()
    if not normalized:
        return ()

    mapping = {
        "D": ("D",),
        "D IF REG AND !WC": ("D",),
        "D IF REG AND WC": ("D",),
        "PA": ("PA",),
        "PB": ("PB",),
        "DIRX": ("DIRA", "DIRB"),
        "OUTX": ("OUTA", "OUTB"),
        "DIRX* + OUTX": ("DIRA", "DIRB", "OUTA", "OUTB"),
        "PER W": ("PA", "PB", "PTRA", "PTRB"),
    }

    if normalized not in mapping:
        raise ValueError(f"Unsupported register-write metadata: {register_write}")

    return mapping[normalized]


def merge_written_registers(register_sets: object) -> list[str]:
    merged: set[str] = set()
    for register_set in register_sets:
        merged.update(register_set)

    return [name for name in WRITTEN_REGISTER_ORDER if name in merged]


def parse_hw_stack_effect(stack_rw: str) -> str:
    normalized = stack_rw.upper()
    if not normalized:
        return "None"

    if normalized == "PUSH":
        return "Push"

    if normalized == "POP":
        return "Pop"

    raise ValueError(f"Unsupported stack metadata: {stack_rw}")


def merge_stack_effects(effects: object) -> str:
    distinct = {effect for effect in effects if effect != "None"}
    if not distinct:
        return "None"

    if len(distinct) != 1:
        raise ValueError(f"Conflicting stack effects: {sorted(distinct)}")

    return distinct.pop()


def build_export_model(workbook_path: Path) -> ExportModel:
    instruction_rows = load_sheet_rows(workbook_path, "Instructions")
    alias_rows = load_sheet_rows(workbook_path, "Aliases")
    all_rows = instruction_rows + alias_rows

    return ExportModel(
        conditionCodes=load_condition_codes(workbook_path),
        modczOperands=load_modcz_operands(workbook_path),
        specialRegisters={name: info for name, info in SPECIAL_REGISTER_INFO.items()},
        flagEffects={name: FLAG_EFFECT_INFO[name] for name in FLAG_EFFECT_ORDER},
        mnemonics=aggregate_instruction_forms(all_rows),
    )


def render_json(model: ExportModel) -> str:
    return json.dumps(asdict(model), indent=2, ensure_ascii=False) + "\n"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Export P2 instruction metadata as JSON from the spreadsheet.")
    parser.add_argument(
        "--workbook",
        type=Path,
        default=DEFAULT_WORKBOOK,
        help=f"Path to the workbook. Defaults to {DEFAULT_WORKBOOK.relative_to(REPO_ROOT)}.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=DEFAULT_OUTPUT,
        help=f"Path to the generated JSON file. Defaults to {DEFAULT_OUTPUT.relative_to(REPO_ROOT)}.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    workbook_path = args.workbook.resolve()
    output_path = args.output.resolve()

    model = build_export_model(workbook_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(render_json(model), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
