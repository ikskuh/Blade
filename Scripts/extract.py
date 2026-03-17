#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
import shlex
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import pandas as pd


SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
DEFAULT_WORKBOOK = REPO_ROOT / "Docs" / "propeller2-instructions.xlsx"
DEFAULT_OUTPUT = REPO_ROOT / "Blade" / "P2InstructionMetadata.g.cs"


NO_REGISTER_EFFECT_MNEMONICS = frozenset({"NOP", "REP", "AUGS", "AUGD"})
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
SPECIAL_REGISTER_NAMES = (
    "PA",
    "PB",
    "PTRA",
    "PTRB",
    "DIRA",
    "DIRB",
    "OUTA",
    "OUTB",
    "INA",
    "INB",
    "IJMP1",
    "IRET1",
    "IJMP2",
    "IRET2",
    "IJMP3",
    "IRET3",
)

FLAG_EFFECT_ORDER = (
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
class OperandInfo:
    role: str
    bit_width: int
    access: str
    supports_immediate_syntax: bool
    uses_immediate_symbol_syntax: bool
    aug_prefix: str


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
            allowed_flag_effects = tokens
            remainder = remainder[: match.start()].rstrip()

    return mnemonic, remainder, allowed_flag_effects


def count_operands(operand_text: str) -> int:
    if not operand_text:
        return 0
    return operand_text.count(",") + 1


def load_prefix_names(workbook_path: Path) -> tuple[tuple[str, ...], tuple[str, ...]]:
    frame = pd.read_excel(workbook_path, sheet_name="Prefixes")
    syntax_column = frame.columns[1]
    alias_column = frame.columns[4]

    names: list[str] = []
    canonical_names: list[str] = []
    for _, row in frame.iterrows():
        syntax = normalize_text(row[syntax_column])
        if not syntax:
            continue

        name = syntax.split(" ", 1)[0].upper()
        names.append(name)
        if normalize_text(row[alias_column]).lower() != "alias":
            canonical_names.append(name)

    return tuple(sorted(set(names))), tuple(sorted(set(canonical_names)))


def load_modcz_operands(workbook_path: Path) -> tuple[tuple[str, ...], tuple[str, ...]]:
    frame = pd.read_excel(workbook_path, sheet_name="MODCZ")
    syntax_column = frame.columns[1]
    alias_column = frame.columns[4]

    names: list[str] = []
    canonical_names: list[str] = []
    for _, row in frame.iterrows():
        name = normalize_text(row[syntax_column]).upper()
        if not name:
            continue

        names.append(name)
        if normalize_text(row[alias_column]).lower() != "alias":
            canonical_names.append(name)

    return tuple(sorted(set(names))), tuple(sorted(set(canonical_names)))


def aggregate_instruction_forms(rows: list[SheetRow]) -> list[dict[str, object]]:
    grouped: dict[tuple[str, int], list[SheetRow]] = {}
    for row in rows:
        grouped.setdefault((row.mnemonic, row.operand_count), []).append(row)

    forms: list[dict[str, object]] = []
    for (mnemonic, operand_count), group_rows in sorted(grouped.items()):
        representative = group_rows[0]
        allowed_flag_effects = sorted(
            {
                effect
                for group_row in group_rows
                for effect in group_row.allowed_flag_effects
            },
            key=lambda effect: FLAG_EFFECT_ORDER.index(effect),
        )

        group_name = representative.group
        upper_group_name = group_name.upper()
        is_call = "CALL" in upper_group_name
        is_return = "RETURN" in upper_group_name
        is_branch = "BRANCH" in upper_group_name and not is_call and not is_return and "REPEAT" not in upper_group_name
        operand_infos = merge_operand_layouts(
            build_operand_infos(row, is_call, is_return, is_branch)
            for row in group_rows
        )
        written_registers = merge_written_registers(parse_written_registers(row.register_write) for row in group_rows)
        hw_stack_effect = merge_stack_effects(parse_hw_stack_effect(row.stack_rw) for row in group_rows)

        forms.append(
            {
                "mnemonic": mnemonic,
                "operand_count": operand_count,
                "allowed_flag_effects": tuple(allowed_flag_effects),
                "operand_infos": operand_infos,
                "written_registers": written_registers,
                "hw_stack_effect": hw_stack_effect,
                "is_call": is_call,
                "is_branch": is_branch,
                "is_return": is_return,
                "has_no_register_effect": mnemonic in NO_REGISTER_EFFECT_MNEMONICS,
                "is_pure_register_local": mnemonic in PURE_REGISTER_LOCAL_MNEMONICS,
            }
        )

    return forms


def build_operand_infos(
    row: SheetRow,
    is_call: bool,
    is_return: bool,
    is_branch: bool,
) -> tuple[OperandInfo, OperandInfo, OperandInfo]:
    operands = split_operands(row.operand_text)
    d_access = infer_d_operand_access(row, operands, is_call, is_return, is_branch)
    symbol_immediate_indices = infer_symbol_immediate_operand_indices(
        row.operand_text,
        row.operand_count,
        row.group,
        row.mnemonic,
    )

    infos = [
        create_operand_info(token, row.encoding, d_access, operand_index in symbol_immediate_indices)
        for operand_index, token in enumerate(operands)
    ]
    while len(infos) < 3:
        infos.append(OperandInfo("None", 0, "None", False, False, "None"))

    return infos[0], infos[1], infos[2]


def split_operands(operand_text: str) -> tuple[str, ...]:
    if not operand_text:
        return ()

    return tuple(part.strip() for part in operand_text.split(","))


def infer_d_operand_access(
    row: SheetRow,
    operands: tuple[str, ...],
    is_call: bool,
    is_return: bool,
    is_branch: bool,
) -> str:
    if not any(infer_operand_role(token) == "D" for token in operands):
        return "None"

    writes_destination = "D" in parse_written_registers(row.register_write)

    if row.mnemonic == "LOC":
        return "Write"

    if is_return:
        return "None"

    if is_call:
        return "Write" if writes_destination else "Read"

    if is_branch:
        return "ReadWrite" if writes_destination else "Read"

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


def create_operand_info(
    token: str,
    encoding: str,
    d_access: str,
    uses_immediate_symbol_syntax: bool,
) -> OperandInfo:
    role = infer_operand_role(token)
    supports_immediate_syntax = "#" in token
    bit_width = infer_operand_bit_width(token, encoding, role)
    access = infer_operand_access(role, d_access)
    aug_prefix = infer_aug_prefix(role, supports_immediate_syntax)
    return OperandInfo(role, bit_width, access, supports_immediate_syntax, uses_immediate_symbol_syntax, aug_prefix)


def infer_symbol_immediate_operand_indices(
    operand_text: str,
    operand_count: int,
    group_name: str,
    mnemonic: str,
) -> frozenset[int]:
    if mnemonic == "LOC":
        return frozenset({operand_count - 1}) if operand_count > 0 else frozenset()

    upper_group_name = group_name.upper()

    if mnemonic == "JMPREL":
        return frozenset()

    if "BRANCH" not in upper_group_name and "CALL" not in upper_group_name:
        return frozenset()

    if "RETURN" in upper_group_name or "REPEAT" in upper_group_name:
        return frozenset()

    if "BRANCH D -" in upper_group_name:
        return frozenset({0}) if operand_count > 0 else frozenset()

    if "BRANCH S -" in upper_group_name or "BRANCH A -" in upper_group_name:
        return frozenset({operand_count - 1}) if operand_count > 0 else frozenset()

    if operand_count == 1:
        return frozenset({0})

    if "#{\\}A" in operand_text or re.search(r"\bA\b", operand_text):
        return frozenset({operand_count - 1}) if operand_count > 0 else frozenset()

    return frozenset()


def infer_operand_role(token: str) -> str:
    normalized = token.upper().replace("{", "").replace("}", "").replace("\\", "")
    if re.fullmatch(r"#?N", normalized):
        return "N"

    if re.search(r"(^|[^A-Z])S(?:/P)?($|[^A-Z])", normalized):
        return "S"

    if re.search(r"(^|[^A-Z])D($|[^A-Z])", normalized):
        return "D"

    return "None"


def infer_operand_bit_width(token: str, encoding: str, role: str) -> int:
    if role == "D":
        return count_encoding_bits(encoding, "D")

    if role == "S":
        return count_encoding_bits(encoding, "S")

    if role == "N":
        return count_encoding_bits(encoding, "N")

    upper_token = token.upper()
    if "#{\\}A" in upper_token or re.search(r"(^|[^A-Z])A($|[^A-Z])", upper_token.replace("\\", "")):
        return count_encoding_bits(encoding, "A")

    if "PA/PB/PTRA/PTRB" in upper_token:
        return count_encoding_bits(encoding, "W")

    return 0


def count_encoding_bits(encoding: str, symbol: str) -> int:
    return encoding.upper().count(symbol.upper())


def infer_operand_access(role: str, d_access: str) -> str:
    if role == "D":
        return d_access

    if role == "S":
        return "Read"

    return "None"


def infer_aug_prefix(role: str, supports_immediate_syntax: bool) -> str:
    if not supports_immediate_syntax:
        return "None"

    if role == "D":
        return "AUGD"

    if role == "S":
        return "AUGS"

    return "None"


def merge_operand_layouts(layouts: object) -> tuple[OperandInfo, OperandInfo, OperandInfo]:
    layout_list = list(layouts)
    if not layout_list:
        default = OperandInfo("None", 0, "None", False, False, "None")
        return default, default, default

    merged: list[OperandInfo] = []
    for operand_index in range(3):
        candidates = [layout[operand_index] for layout in layout_list]
        role = candidates[0].role if all(candidate.role == candidates[0].role for candidate in candidates) else "None"
        bit_width = candidates[0].bit_width if all(candidate.bit_width == candidates[0].bit_width for candidate in candidates) else max(candidate.bit_width for candidate in candidates)
        supports_immediate_syntax = any(candidate.supports_immediate_syntax for candidate in candidates)
        uses_immediate_symbol_syntax = any(candidate.uses_immediate_symbol_syntax for candidate in candidates)
        aug_prefix = candidates[0].aug_prefix if all(candidate.aug_prefix == candidates[0].aug_prefix for candidate in candidates) else "None"
        if not supports_immediate_syntax:
            aug_prefix = "None"

        access = candidates[0].access
        for candidate in candidates[1:]:
            access = merge_operand_access(access, candidate.access)

        merged.append(OperandInfo(role, bit_width, access, supports_immediate_syntax, uses_immediate_symbol_syntax, aug_prefix))

    return merged[0], merged[1], merged[2]


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


def parse_written_registers(register_write: str) -> frozenset[str]:
    normalized = register_write.upper()
    if not normalized:
        return frozenset()

    mapping = {
        "D": frozenset({"D"}),
        "D IF REG AND !WC": frozenset({"D"}),
        "D IF REG AND WC": frozenset({"D"}),
        "PA": frozenset({"PA"}),
        "PB": frozenset({"PB"}),
        "DIRX": frozenset({"DIRA", "DIRB"}),
        "OUTX": frozenset({"OUTA", "OUTB"}),
        "DIRX* + OUTX": frozenset({"DIRA", "DIRB", "OUTA", "OUTB"}),
        "PER W": frozenset({"PA", "PB", "PTRA", "PTRB"}),
    }

    if normalized not in mapping:
        raise ValueError(f"Unsupported register-write metadata: {register_write}")

    return mapping[normalized]


def merge_written_registers(register_sets: object) -> frozenset[str]:
    merged: set[str] = set()
    for register_set in register_sets:
        merged.update(register_set)

    return frozenset(sorted(merged))


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


def render_generated_source(
    forms: list[dict[str, object]],
    valid_mnemonics: tuple[str, ...],
    condition_prefixes: tuple[str, ...],
    canonical_condition_prefixes: tuple[str, ...],
    modcz_operands: tuple[str, ...],
    canonical_modcz_operands: tuple[str, ...],
    generated_at: str,
    command_line: str,
    workbook_path: Path,
) -> str:
    all_flag_effects = sorted(
        {
            effect
            for form in forms
            for effect in form["allowed_flag_effects"]
        },
        key=lambda effect: FLAG_EFFECT_ORDER.index(effect),
    )

    lines: list[str] = []
    lines.extend(
        [
            "// <auto-generated>",
            f"// Generated from {workbook_path.relative_to(REPO_ROOT).as_posix()}.",
            f"// Generated at: {generated_at}",
            f"// Command: {command_line}",
            "// Do not edit this file manually. Regenerate it instead.",
            "// </auto-generated>",
            "",
            "#nullable enable",
            "",
            "using System;",
            "using System.Collections.Frozen;",
            "using System.Collections.Generic;",
            "",
            "namespace Blade;",
            "",
            "public enum P2OperandAccess",
            "{",
            "    None,",
            "    Read,",
            "    Write,",
            "    ReadWrite,",
            "}",
            "",
            "public enum P2OperandRole",
            "{",
            "    None,",
            "    D,",
            "    S,",
            "    N,",
            "}",
            "",
            "public enum P2AugPrefixKind",
            "{",
            "    None,",
            "    AUGD,",
            "    AUGS,",
            "}",
            "",
            "[Flags]",
            "public enum P2WrittenRegister",
            "{",
            "    None = 0,",
            "    D = 1 << 0,",
            "    PA = 1 << 1,",
            "    PB = 1 << 2,",
            "    PTRA = 1 << 3,",
            "    PTRB = 1 << 4,",
            "    DIRA = 1 << 5,",
            "    DIRB = 1 << 6,",
            "    OUTA = 1 << 7,",
            "    OUTB = 1 << 8,",
            "}",
            "",
            "public enum P2HwStackEffect",
            "{",
            "    None,",
            "    Push,",
            "    Pop,",
            "}",
            "",
            "[Flags]",
            "public enum P2FlagEffect",
            "{",
            "    None = 0,",
        ]
    )

    for index, effect in enumerate(FLAG_EFFECT_ORDER):
        lines.append(f"    {effect} = 1 << {index},")

    lines.extend(
        [
            "}",
            "",
            "public readonly record struct P2InstructionOperandInfo(",
            "    P2OperandRole Role,",
            "    int BitWidth,",
            "    P2OperandAccess Access,",
            "    bool SupportsImmediateSyntax,",
            "    bool UsesImmediateSymbolSyntax,",
            "    P2AugPrefixKind AugPrefix);",
            "",
            "public readonly record struct P2InstructionFormInfo(",
            "    string Mnemonic,",
            "    int OperandCount,",
            "    P2InstructionOperandInfo Operand0,",
            "    P2InstructionOperandInfo Operand1,",
            "    P2InstructionOperandInfo Operand2,",
            "    P2WrittenRegister WrittenRegisters,",
            "    P2HwStackEffect HwStackEffect,",
            "    P2FlagEffect AllowedFlagEffects,",
            "    bool IsCall,",
            "    bool IsBranch,",
            "    bool IsReturn,",
            "    bool HasNoRegisterEffect,",
            "    bool IsPureRegisterLocal)",
            "{",
            "    public bool IsControlFlow => IsCall || IsBranch || IsReturn;",
            "",
            "    public P2InstructionOperandInfo GetOperandInfo(int operandIndex)",
            "        => operandIndex switch",
            "        {",
            "            0 => Operand0,",
            "            1 => Operand1,",
            "            2 => Operand2,",
            "            _ => default,",
            "        };",
            "}",
            "",
            "public static class P2InstructionMetadata",
            "{",
            "    private static readonly FrozenDictionary<string, FrozenDictionary<int, P2InstructionFormInfo>> FormsByMnemonic =",
            "        new Dictionary<string, FrozenDictionary<int, P2InstructionFormInfo>>(StringComparer.OrdinalIgnoreCase)",
            "        {",
        ]
    )

    grouped_forms: dict[str, list[dict[str, object]]] = {}
    for form in forms:
        grouped_forms.setdefault(form["mnemonic"], []).append(form)

    for mnemonic in sorted(grouped_forms):
        lines.append(f'            ["{mnemonic}"] = new Dictionary<int, P2InstructionFormInfo>()')
        lines.append("            {")
        for form in sorted(grouped_forms[mnemonic], key=lambda item: item["operand_count"]):
            flag_expr = render_flag_effect_mask(form["allowed_flag_effects"])
            lines.append(
                "                "
                f'[{form["operand_count"]}] = new P2InstructionFormInfo('
                f'"{form["mnemonic"]}", '
                f'{form["operand_count"]}, '
                f'{render_operand_info(form["operand_infos"][0])}, '
                f'{render_operand_info(form["operand_infos"][1])}, '
                f'{render_operand_info(form["operand_infos"][2])}, '
                f'{render_written_registers(form["written_registers"])}, '
                f'P2HwStackEffect.{form["hw_stack_effect"]}, '
                f"{flag_expr}, "
                f'{render_bool(form["is_call"])}, '
                f'{render_bool(form["is_branch"])}, '
                f'{render_bool(form["is_return"])}, '
                f'{render_bool(form["has_no_register_effect"])}, '
                f'{render_bool(form["is_pure_register_local"])}),'
            )
        lines.append("            }.ToFrozenDictionary(),")

    lines.extend(
        [
            "        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);",
            "",
            "    private static readonly FrozenSet<string> ValidMnemonics =",
            "        new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "        {",
        ]
    )
    for mnemonic in valid_mnemonics:
        lines.append(f'            "{mnemonic}",')
    lines.extend(
        [
            "        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);",
            "",
            "    private static readonly FrozenSet<string> ConditionPrefixes =",
            "        new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "        {",
        ]
    )
    for prefix in condition_prefixes:
        lines.append(f'            "{prefix}",')
    lines.extend(
        [
            "        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);",
            "",
            "    private static readonly FrozenSet<string> CanonicalConditionPrefixes =",
            "        new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "        {",
        ]
    )
    for prefix in canonical_condition_prefixes:
        lines.append(f'            "{prefix}",')
    lines.extend(
        [
            "        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);",
            "",
            "    private static readonly FrozenSet<string> ModczOperands =",
            "        new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "        {",
        ]
    )
    for operand in modcz_operands:
        lines.append(f'            "{operand}",')
    lines.extend(
        [
            "        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);",
            "",
            "    private static readonly FrozenSet<string> CanonicalModczOperands =",
            "        new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "        {",
        ]
    )
    for operand in canonical_modcz_operands:
        lines.append(f'            "{operand}",')
    lines.extend(
        [
            "        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);",
            "",
            "    private static readonly FrozenSet<string> ValidFlagEffects =",
            "        new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "        {",
        ]
    )
    for effect in all_flag_effects:
        lines.append(f'            "{effect}",')
    lines.extend(
        [
            "        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);",
            "",
            "    private static readonly FrozenSet<string> SpecialRegisterNames =",
            "        new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "        {",
        ]
    )
    for name in SPECIAL_REGISTER_NAMES:
        lines.append(f'            "{name}",')
    lines.extend(
        [
            "        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);",
            "",
            "    public static bool IsValidInstruction(string mnemonic)",
            "        => ValidMnemonics.Contains(mnemonic);",
            "",
            "    public static bool TryGetInstructionForm(string mnemonic, int operandCount, out P2InstructionFormInfo info)",
            "    {",
            "        info = default;",
            "        if (!FormsByMnemonic.TryGetValue(mnemonic, out FrozenDictionary<int, P2InstructionFormInfo>? formsByOperandCount))",
            "            return false;",
            "",
            "        return formsByOperandCount.TryGetValue(operandCount, out info);",
            "    }",
            "",
            "    public static bool IsValidConditionPrefix(string name)",
            "        => ConditionPrefixes.Contains(name);",
            "",
            "    public static bool IsCanonicalConditionPrefix(string name)",
            "        => CanonicalConditionPrefixes.Contains(name);",
            "",
            "    public static bool IsValidModczOperand(string name)",
            "        => ModczOperands.Contains(name);",
            "",
            "    public static bool IsCanonicalModczOperand(string name)",
            "        => CanonicalModczOperands.Contains(name);",
            "",
            "    public static bool IsValidFlagEffect(string name)",
            "        => ValidFlagEffects.Contains(name);",
            "",
            "    public static bool AllowsFlagEffect(string mnemonic, int operandCount, string? name)",
            "    {",
            "        if (string.IsNullOrWhiteSpace(name))",
            "            return true;",
            "",
            "        if (!TryGetInstructionForm(mnemonic, operandCount, out P2InstructionFormInfo info))",
            "            return false;",
            "",
            "        if (!TryParseFlagEffect(name, out P2FlagEffect effect))",
            "            return false;",
            "",
            "        return (info.AllowedFlagEffects & effect) == effect;",
            "    }",
            "",
            "    public static bool TryParseFlagEffect(string name, out P2FlagEffect effect)",
            "    {",
            "        effect = name.ToUpperInvariant() switch",
            "        {",
        ]
    )
    for effect in FLAG_EFFECT_ORDER:
        lines.append(f'            "{effect}" => P2FlagEffect.{effect},')
    lines.extend(
        [
            "            _ => P2FlagEffect.None,",
            "        };",
            "",
            "        return effect != P2FlagEffect.None;",
            "    }",
            "",
            "    public static bool IsSpecialRegisterName(string name)",
            "        => SpecialRegisterNames.Contains(name);",
            "",
            "    public static bool IsCall(string mnemonic, int operandCount)",
            "        => TryGetInstructionForm(mnemonic, operandCount, out P2InstructionFormInfo info) && info.IsCall;",
            "",
            "    public static bool IsReturn(string mnemonic, int operandCount)",
            "        => TryGetInstructionForm(mnemonic, operandCount, out P2InstructionFormInfo info) && info.IsReturn;",
            "",
            "    public static bool IsControlFlow(string mnemonic, int operandCount)",
            "        => TryGetInstructionForm(mnemonic, operandCount, out P2InstructionFormInfo info) && info.IsControlFlow;",
            "",
            "    public static bool HasNoRegisterEffect(string mnemonic, int operandCount)",
            "        => TryGetInstructionForm(mnemonic, operandCount, out P2InstructionFormInfo info) && info.HasNoRegisterEffect;",
            "",
            "    public static bool IsPureRegisterLocal(string mnemonic, int operandCount)",
            "        => TryGetInstructionForm(mnemonic, operandCount, out P2InstructionFormInfo info) && info.IsPureRegisterLocal;",
            "",
            "    public static P2InstructionOperandInfo GetOperandInfo(string mnemonic, int operandCount, int operandIndex)",
            "    {",
            "        if (!TryGetInstructionForm(mnemonic, operandCount, out P2InstructionFormInfo info))",
            "            return default;",
            "",
            "        if (operandIndex < 0 || operandIndex >= operandCount)",
            "            return default;",
            "",
            "        return info.GetOperandInfo(operandIndex);",
            "    }",
            "",
            "    public static bool UsesImmediateSyntax(string mnemonic, int operandCount, int operandIndex)",
            "        => GetOperandInfo(mnemonic, operandCount, operandIndex).SupportsImmediateSyntax;",
            "",
            "    public static bool UsesImmediateSymbolSyntax(string mnemonic, int operandCount, int operandIndex)",
            "        => GetOperandInfo(mnemonic, operandCount, operandIndex).UsesImmediateSymbolSyntax;",
            "",
            "    public static P2OperandAccess GetOperandAccess(string mnemonic, int operandCount, int operandIndex)",
            "        => GetOperandInfo(mnemonic, operandCount, operandIndex).Access;",
            "}",
            "",
            "#nullable restore",
            "",
        ]
    )

    return "\n".join(lines)


def render_bool(value: object) -> str:
    return "true" if value else "false"


def render_flag_effect_mask(flag_effects: tuple[str, ...]) -> str:
    if not flag_effects:
        return "P2FlagEffect.None"

    return " | ".join(f"P2FlagEffect.{effect}" for effect in flag_effects)


def render_operand_info(info: OperandInfo) -> str:
    return (
        "new P2InstructionOperandInfo("
        f"P2OperandRole.{info.role}, "
        f"{info.bit_width}, "
        f"P2OperandAccess.{info.access}, "
        f"{render_bool(info.supports_immediate_syntax)}, "
        f"{render_bool(info.uses_immediate_symbol_syntax)}, "
        f"P2AugPrefixKind.{info.aug_prefix})"
    )


def render_written_registers(registers: frozenset[str]) -> str:
    if not registers:
        return "P2WrittenRegister.None"

    return " | ".join(f"P2WrittenRegister.{register}" for register in sorted(registers))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate P2 instruction metadata C# source from the spreadsheet.")
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
        help=f"Path to the generated C# file. Defaults to {DEFAULT_OUTPUT.relative_to(REPO_ROOT)}.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    workbook_path = args.workbook.resolve()
    output_path = args.output.resolve()

    instruction_rows = load_sheet_rows(workbook_path, "Instructions")
    alias_rows = load_sheet_rows(workbook_path, "Aliases")
    all_rows = instruction_rows + alias_rows

    valid_mnemonics = tuple(sorted({row.mnemonic for row in all_rows}))
    condition_prefixes, canonical_condition_prefixes = load_prefix_names(workbook_path)
    modcz_operands, canonical_modcz_operands = load_modcz_operands(workbook_path)
    forms = aggregate_instruction_forms(all_rows)

    generated_at = datetime.now(timezone.utc).replace(microsecond=0).isoformat()
    command_line = " ".join(shlex.quote(part) for part in [sys.executable, *sys.argv])
    source = render_generated_source(
        forms=forms,
        valid_mnemonics=valid_mnemonics,
        condition_prefixes=condition_prefixes,
        canonical_condition_prefixes=canonical_condition_prefixes,
        modcz_operands=modcz_operands,
        canonical_modcz_operands=canonical_modcz_operands,
        generated_at=generated_at,
        command_line=command_line,
        workbook_path=workbook_path,
    )

    output_path.write_text(source + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
