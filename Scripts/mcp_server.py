#!/usr/bin/env python3

import base64
import csv
import difflib
from enum import Enum
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from typing import Any, Iterable, Literal
from urllib.parse import unquote, urlparse

try:
    import pandas as pd
except ImportError:
    pd = None

from pydantic import BaseModel, Field, ValidationError

from mcp.server.fastmcp import FastMCP

REPO_ROOT = Path(__file__).parent.parent
COMPILER_PATH = REPO_ROOT / "Blade/bin/Debug/net10.0/blade"
COMPILER_PROJECT_PATH = REPO_ROOT / "Blade/blade.csproj"
REGRESSIONS_PROJECT_PATH = REPO_ROOT / "Blade.Regressions"
REGRESSIONS_BINARY_PATH = REPO_ROOT / "Blade.Regressions/bin/Debug/net10.0/Blade.Regressions"
WORKBOOK_PATH = REPO_ROOT / "Docs/propeller2-instructions.xlsx"
INSTRUCTION_CSV_PATH = REPO_ROOT / "Docs/Parallax Propeller 2 Instructions v35 - Rev B_C Silicon.csv"
CONFIG_PATH = REPO_ROOT / ".blade_mcp.json"

FAILED_REGRESSION_OUTCOMES = frozenset({"fail", "unexpectedPass", "hwFailed"})

ARTIFACT_NAME_TO_FILE = {
    "summary": "summary.txt",
    "issues": "issues.txt",
    "diagnostics": "diagnostics.txt",
    "bound": "bound.txt",
    "mir-preopt": "mir-preopt.txt",
    "mir": "mir.txt",
    "lir-preopt": "lir-preopt.txt",
    "lir": "lir.txt",
    "asmir-preopt": "asmir-preopt.txt",
    "asmir": "asmir.txt",
    "final-asm": "final-asm.txt",
    "final-spin2": "final.spin2",
    "hardware-bin": "hardware.bin",
    "fixture-body": "fixture-body.txt",
}
ARTIFACT_FILE_TO_NAME = {value: key for key, value in ARTIFACT_NAME_TO_FILE.items()}


class RequestedDump(Enum):
    bound = "bound"
    mir_preopt = "mir-preopt"
    mir = "mir"
    lir_preopt = "lir-preopt"
    lir = "lir"
    asmir_preopt = "asmir-preopt"
    asmir = "asmir"
    final_asm = "final-asm"


class CompileParameter(BaseModel):
    file_path: str = Field(
        description="Path to the source file (relative to the repository root)",
    )
    dumps: set[RequestedDump] | bool = Field(
        description="Requested compiler dumps, or true for all dumps.",
        default=False,
    )
    metrics: bool = Field(
        description="Should the output contain compiler metrics",
        default=False,
    )
    result: bool = Field(
        description="Should the output include generated code",
        default=False,
    )
    arguments: list[str] = Field(
        description="Additional command line arguments passed to the compiler",
        default_factory=list,
    )
    modules: dict[str, str] = Field(
        description="A collection of module names to repo-relative module paths",
        default_factory=dict,
    )


class CompilerDumps(BaseModel):
    bound: str | None
    mir_preopt: str | None = Field(alias="mir-preopt")
    mir: str | None
    lir_preopt: str | None = Field(alias="lir-preopt")
    lir: str | None
    asmir_preopt: str | None = Field(alias="asmir-preopt")
    asmir: str | None
    final_asm: str | None = Field(alias="final-asm", default=None)


class Diagnostic(BaseModel):
    file: str
    line: int
    code: str
    message: str


class Metrics(BaseModel):
    token_count: int
    member_count: int
    bound_function_count: int
    mir_function_count: int
    time_ms: float


class RawCompilerResult(BaseModel):
    success: bool
    diagnostics: list[Diagnostic]
    dumps: CompilerDumps
    result: str | None
    metrics: Metrics | None


class CompilerOutput(BaseModel):
    success: bool = Field(description="The compiler invocation succeeded")
    diagnostics: list[str] = Field(description="Formatted diagnostic messages")
    dumps: CompilerDumps | None = Field(description="The available compiler dumps")
    result: str | None = Field(description="The final generated assembler code")
    metrics: Metrics | None = Field(description="Compiler metrics")


class McpOutput(BaseModel):
    success: Literal[False] = Field(
        description="The tool invocation failed",
        default=False,
    )
    reason: str = Field(description="The failure reason")
    exception: str | None = Field(
        default=None,
        description="The exception dump, if available",
    )


class HardwareConfig(BaseModel):
    serial_port: str | None = None


class BladeMcpConfig(BaseModel):
    hardware: HardwareConfig = Field(default_factory=HardwareConfig)


class RegressionFixtureOutput(BaseModel):
    relative_path: str
    outcome: str
    summary: str
    details: list[str]
    hardware_attempted: bool
    hardware_available: bool
    artifact_names: list[str]
    artifact_directory: str | None


class RegressionSuiteOutput(BaseModel):
    succeeded: bool
    fixture_count: int
    pass_count: int
    fail_count: int
    xfail_count: int
    unexpected_pass_count: int
    skip_count: int
    hw_failed_count: int
    hardware_available: bool
    failed_paths: list[str]


class RegressionArtifactContent(BaseModel):
    relative_path: str
    artifact: str
    kind: Literal["text", "binary"]
    text: str | None = None
    base64_data: str | None = None
    byte_count: int | None = None


class BuildDiagnostic(BaseModel):
    severity: Literal["warning", "error"]
    code: str
    message: str
    file: str | None
    line: int | None
    column: int | None
    end_line: int | None = None
    end_column: int | None = None


class BuildCompilerOutput(BaseModel):
    success: bool
    warning_count: int
    error_count: int
    diagnostics: list[BuildDiagnostic]


class InstructionFormData(BaseModel):
    syntax: str
    description: str
    cog_exec_timing: str
    hub_exec_timing: str
    registers_read: list[str]
    registers_modified: list[str]
    registers_written: list[str]
    written_memories: list[str]
    is_alias: bool


class LatestRegressionRun(BaseModel):
    fixture_results: dict[str, RegressionFixtureOutput]
    failed_paths: list[str]


def fmt_diag(diag: Diagnostic) -> str:
    path = (
        Path(os.path.normpath((REPO_ROOT / diag.file).absolute()))
        .relative_to(REPO_ROOT)
        .as_posix()
    )
    return f"{path}:{diag.line}: {diag.code}: {diag.message}"


mcp = FastMCP("BladeCompilerServer")

_latest_regression_run: LatestRegressionRun | None = None
_instruction_index: dict[str, Any] | None = None


def map_path(raw_path: str) -> Path | None:
    path = (REPO_ROOT / raw_path).resolve(strict=False)
    if path != REPO_ROOT and REPO_ROOT not in path.parents:
        return None
    return path


def repo_relative_path(path: Path) -> str:
    return path.resolve(strict=False).relative_to(REPO_ROOT).as_posix()


def contains_param(items: Iterable[str], name: str) -> bool:
    for item in items:
        if item == name or item.startswith(name + "="):
            return True
    return False


def map_argument(arg: str) -> str | McpOutput:
    if not arg.startswith("--runtime="):
        return arg

    raw_path = arg[len("--runtime=") :]
    path = map_path(raw_path)
    if path is None:
        return McpOutput(
            reason=f"runtime template {raw_path!r} is a path outside the repository"
        )
    if not path.is_file():
        return McpOutput(reason=f"runtime template {raw_path!r} does not exist")

    return f"--runtime={path.as_posix()}"


def load_mcp_config() -> BladeMcpConfig | None:
    if not CONFIG_PATH.is_file():
        return None

    try:
        return BladeMcpConfig.model_validate_json(CONFIG_PATH.read_text(encoding="utf-8"))
    except (OSError, ValidationError, json.JSONDecodeError):
        return None


def get_configured_serial_port() -> str | None:
    config = load_mcp_config()
    if config is None:
        return None

    serial_port = config.hardware.serial_port
    if serial_port is None or not serial_port.strip():
        return None

    return serial_port.strip()


def is_serial_port_available(serial_port: str | None) -> bool:
    if serial_port is None:
        return False
    return Path(serial_port).exists()


def get_hw_availability() -> tuple[bool, str | None]:
    serial_port = get_configured_serial_port()
    return is_serial_port_available(serial_port), serial_port


def run_command(argv: list[str], cwd: Path = REPO_ROOT) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        argv,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=cwd,
        check=False,
        encoding="utf-8",
    )


def resolve_same_timing(value: str, fallback: str) -> str:
    normalized = normalize_text(value)
    if normalized.lower() == "same":
        return fallback
    return normalized


def normalize_text(value: object) -> str:
    if value is None:
        return ""

    if pd is not None:
        try:
            if pd.isna(value):
                return ""
        except TypeError:
            pass

    text = str(value).replace("\xa0", " ")
    text = text.replace("…", "...")
    return re.sub(r"\s+", " ", text).strip()


def parse_assembly_syntax(syntax: str) -> tuple[str, str]:
    parts = syntax.split(" ", 1)
    mnemonic = parts[0].upper()
    remainder = parts[1].strip() if len(parts) > 1 else ""

    match = re.search(r"(?:\{([^}]+)\}|([A-Z]+(?:/[A-Z]+)+))$", remainder)
    if match is not None:
        token_text = match.group(1) or match.group(2) or ""
        tokens = tuple(part.strip().upper() for part in token_text.split("/") if part.strip())
        flag_effects = {"WC", "WZ", "WCZ", "ANDC", "ANDZ", "ORC", "ORZ", "XORC", "XORZ"}
        if tokens and all(token in flag_effects for token in tokens):
            remainder = remainder[: match.start()].rstrip()

    return mnemonic, remainder


def split_operands(operand_text: str) -> tuple[str, ...]:
    if not operand_text:
        return ()
    return tuple(part.strip() for part in operand_text.split(","))


def infer_operand_role(token: str) -> str:
    normalized = token.upper().replace("{", "").replace("}", "").replace("\\", "")
    if re.fullmatch(r"#?N", normalized):
        return "N"
    if re.search(r"(^|[^A-Z])S(?:/P)?($|[^A-Z])", normalized):
        return "S"
    if re.search(r"(^|[^A-Z])D($|[^A-Z])", normalized):
        return "D"
    return "None"


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
        and description.startswith(
            (
                "Add",
                "Subtract",
                "Increment",
                "Decrement",
                "Force",
                "Sum",
                "Mux",
                "Move bytes",
            )
        )
    )


def parse_written_registers(register_write: str) -> list[str]:
    normalized = register_write.upper()
    if not normalized:
        return []

    mapping = {
        "D": ["D"],
        "D IF REG AND !WC": ["D"],
        "D IF REG AND WC": ["D"],
        "PA": ["PA"],
        "PB": ["PB"],
        "DIRX": ["DIRA", "DIRB"],
        "OUTX": ["OUTA", "OUTB"],
        "DIRX* + OUTX": ["DIRA", "DIRB", "OUTA", "OUTB"],
        "PER W": ["PA", "PB", "PTRA", "PTRB"],
    }
    return list(mapping.get(normalized, []))


def infer_d_operand_access(
    mnemonic: str,
    group_name: str,
    description: str,
    register_write: str,
    operands: tuple[str, ...],
) -> str:
    if not any(infer_operand_role(token) == "D" for token in operands):
        return "None"

    writes_destination = "D" in parse_written_registers(register_write)
    upper_group_name = group_name.upper()
    is_call = "CALL" in upper_group_name
    is_return = "RETURN" in upper_group_name
    is_branch = (
        "BRANCH" in upper_group_name
        and not is_call
        and not is_return
        and "REPEAT" not in upper_group_name
    )

    if mnemonic == "LOC":
        return "Write"
    if is_return:
        return "None"
    if is_call:
        return "Write" if writes_destination else "Read"
    if is_branch:
        return "ReadWrite" if writes_destination else "Read"
    if not writes_destination:
        return "Read"
    if reads_existing_destination(description):
        return "ReadWrite"
    return "Write"


def parse_register_usage(
    mnemonic: str,
    group_name: str,
    description: str,
    register_write: str,
    operand_text: str,
) -> tuple[list[str], list[str], list[str]]:
    operands = split_operands(operand_text)
    d_access = infer_d_operand_access(mnemonic, group_name, description, register_write, operands)

    reads: list[str] = []
    modified: list[str] = []
    written = parse_written_registers(register_write)

    for operand in operands:
        role = infer_operand_role(operand)
        if role == "D":
            if d_access in ("Read", "ReadWrite"):
                reads.append("D")
            if d_access == "ReadWrite":
                modified.append("D")
        elif role == "S":
            reads.append("S")

    if "D" in written and d_access == "ReadWrite" and "D" not in modified:
        modified.append("D")

    return sorted(set(reads)), sorted(set(modified)), sorted(set(written))


def parse_written_memories(hub_rw: str, stack_rw: str) -> list[str]:
    written: list[str] = []

    normalized_hub = normalize_text(hub_rw).upper()
    if "WRITE" in normalized_hub:
        written.append("Hub")

    normalized_stack = normalize_text(stack_rw).upper()
    if normalized_stack == "PUSH":
        written.append("HardwareStack")

    return written


def load_instruction_rows_from_workbook() -> list[dict[str, str]]:
    if pd is None or not WORKBOOK_PATH.is_file():
        return []

    rows: list[dict[str, str]] = []
    try:
        for sheet_name in ("Instructions", "Aliases"):
            frame = pd.read_excel(WORKBOOK_PATH, sheet_name=sheet_name)
            syntax_column = frame.columns[1]
            group_column = frame.columns[2]
            alias_column = frame.columns[4]
            description_column = frame.columns[5]
            cog_exec_column = frame.columns[7]
            hub_exec_column = frame.columns[8]
            register_write_column = frame.columns[11]
            hub_rw_column = frame.columns[12]
            stack_rw_column = frame.columns[13]

            for _, row in frame.iterrows():
                syntax = normalize_text(row[syntax_column])
                if not syntax:
                    continue

                rows.append(
                    {
                        "syntax": syntax,
                        "group": normalize_text(row[group_column]),
                        "description": normalize_text(row[description_column]),
                        "cog_exec_timing": normalize_text(row[cog_exec_column]),
                        "hub_exec_timing": normalize_text(row[hub_exec_column]),
                        "register_write": normalize_text(row[register_write_column]),
                        "hub_rw": normalize_text(row[hub_rw_column]),
                        "stack_rw": normalize_text(row[stack_rw_column]),
                        "is_alias": normalize_text(row[alias_column]).lower() == "alias",
                    }
                )
    except Exception:
        return []

    return rows


def load_instruction_rows_from_csv() -> list[dict[str, str]]:
    if not INSTRUCTION_CSV_PATH.is_file():
        return []

    rows: list[dict[str, str]] = []
    with INSTRUCTION_CSV_PATH.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.reader(handle)
        next(reader, None)
        for row in reader:
            if len(row) < 14:
                continue

            syntax = normalize_text(row[1])
            if not syntax:
                continue

            rows.append(
                {
                    "syntax": syntax,
                    "group": normalize_text(row[2]),
                    "description": normalize_text(row[5]),
                    "cog_exec_timing": normalize_text(row[7]),
                    "hub_exec_timing": normalize_text(row[8]),
                    "register_write": normalize_text(row[11]),
                    "hub_rw": normalize_text(row[12]),
                    "stack_rw": normalize_text(row[13]),
                    "is_alias": normalize_text(row[4]).lower() == "alias",
                }
            )

    return rows


def canonical_instruction_form_key(form: str) -> str:
    return normalize_text(form).upper()


def build_instruction_index() -> dict[str, Any]:
    rows = load_instruction_rows_from_workbook()
    if not rows:
        rows = load_instruction_rows_from_csv()

    forms_by_key: dict[str, InstructionFormData] = {}
    forms_by_mnemonic: dict[str, list[str]] = {}
    search_rows: list[tuple[str, str, str]] = []

    for row in rows:
        mnemonic, operand_text = parse_assembly_syntax(row["syntax"])
        canonical_form = mnemonic if not operand_text else f"{mnemonic} {operand_text}"
        key = canonical_instruction_form_key(canonical_form)

        registers_read, registers_modified, registers_written = parse_register_usage(
            mnemonic,
            row["group"],
            row["description"],
            row["register_write"],
            operand_text,
        )
        written_memories = parse_written_memories(row["hub_rw"], row["stack_rw"])
        cog_exec_timing = normalize_text(row["cog_exec_timing"])
        hub_exec_timing = resolve_same_timing(row["hub_exec_timing"], cog_exec_timing)

        forms_by_key[key] = InstructionFormData(
            syntax=canonical_form,
            description=row["description"],
            cog_exec_timing=cog_exec_timing,
            hub_exec_timing=hub_exec_timing,
            registers_read=registers_read,
            registers_modified=registers_modified,
            registers_written=registers_written,
            written_memories=written_memories,
            is_alias=bool(row["is_alias"]),
        )
        forms_by_mnemonic.setdefault(mnemonic, []).append(canonical_form)
        search_rows.append((canonical_form, mnemonic, row["description"]))

    for forms in forms_by_mnemonic.values():
        forms.sort()

    return {
        "forms_by_key": forms_by_key,
        "forms_by_mnemonic": forms_by_mnemonic,
        "search_rows": search_rows,
    }


def get_instruction_index() -> dict[str, Any]:
    global _instruction_index
    if _instruction_index is None:
        _instruction_index = build_instruction_index()
    return _instruction_index


def artifact_names_for_directory(artifact_directory: str | None) -> list[str]:
    if artifact_directory is None:
        return []

    directory = Path(artifact_directory)
    if not directory.is_dir():
        return []

    names = [
        ARTIFACT_FILE_TO_NAME[path.name]
        for path in directory.iterdir()
        if path.is_file() and path.name in ARTIFACT_FILE_TO_NAME
    ]
    return sorted(names)


def build_fixture_output(raw_fixture_result: dict[str, Any], hardware_available: bool) -> RegressionFixtureOutput:
    artifact_directory = raw_fixture_result.get("artifactDirectoryPath")
    return RegressionFixtureOutput(
        relative_path=str(raw_fixture_result["relativePath"]),
        outcome=str(raw_fixture_result["outcome"]),
        summary=str(raw_fixture_result["summary"]),
        details=[str(item) for item in raw_fixture_result.get("details", [])],
        hardware_attempted=bool(raw_fixture_result.get("hardwareAttempted", False)),
        hardware_available=hardware_available,
        artifact_names=artifact_names_for_directory(artifact_directory),
        artifact_directory=artifact_directory,
    )


def parse_regression_run_output(
    completed: subprocess.CompletedProcess[str],
    hardware_available: bool,
) -> tuple[RegressionSuiteOutput, LatestRegressionRun] | McpOutput:
    try:
        raw = json.loads(completed.stdout)
    except json.JSONDecodeError as err:
        detail = completed.stderr.strip() or completed.stdout.strip()
        return McpOutput(reason="Unexpected regression runner output", exception=f"{err}: {detail}")

    fixture_results_raw = raw.get("fixtureResults")
    if not isinstance(fixture_results_raw, list):
        return McpOutput(reason="Regression runner JSON did not include fixture results")

    fixture_results = [
        build_fixture_output(item, hardware_available)
        for item in fixture_results_raw
        if isinstance(item, dict)
    ]
    failed_paths = [
        result.relative_path
        for result in fixture_results
        if result.outcome in FAILED_REGRESSION_OUTCOMES
    ]

    suite_output = RegressionSuiteOutput(
        succeeded=bool(raw.get("succeeded", completed.returncode == 0)),
        fixture_count=len(fixture_results),
        pass_count=int(raw.get("passCount", 0)),
        fail_count=int(raw.get("failCount", 0)),
        xfail_count=int(raw.get("xFailCount", 0)),
        unexpected_pass_count=int(raw.get("unexpectedPassCount", 0)),
        skip_count=int(raw.get("skipCount", 0)),
        hw_failed_count=int(raw.get("hwFailedCount", 0)),
        hardware_available=hardware_available,
        failed_paths=failed_paths,
    )
    latest_run = LatestRegressionRun(
        fixture_results={result.relative_path: result for result in fixture_results},
        failed_paths=failed_paths,
    )
    return suite_output, latest_run


def run_regressions(filters: list[str], with_hardware: bool) -> tuple[RegressionSuiteOutput, LatestRegressionRun] | McpOutput:
    hardware_available, serial_port = get_hw_availability()
    if not REGRESSIONS_BINARY_PATH.is_file():
        return McpOutput(
            reason="Blade.Regressions binary is not built",
            exception=f"expected executable at {REGRESSIONS_BINARY_PATH.as_posix()}",
        )

    argv = [
        str(REGRESSIONS_BINARY_PATH),
        "--json",
    ]
    if with_hardware and hardware_available and serial_port is not None:
        argv.extend(["--hw-port", serial_port])
    else:
        argv.extend(["--hw-port", ""])
    argv.extend(filters)

    try:
        completed = run_command(argv)
    except Exception as err:
        return McpOutput(reason="Regression runner invocation failed", exception=str(err))

    parsed = parse_regression_run_output(completed, hardware_available)
    if isinstance(parsed, McpOutput):
        return parsed

    return parsed


def select_fixture_result(
    latest_run: LatestRegressionRun,
    requested_relative_path: str,
) -> RegressionFixtureOutput | None:
    if requested_relative_path in latest_run.fixture_results:
        return latest_run.fixture_results[requested_relative_path]

    if len(latest_run.fixture_results) == 1:
        return next(iter(latest_run.fixture_results.values()))

    return None


def update_latest_regression_run(latest_run: LatestRegressionRun) -> None:
    global _latest_regression_run
    _latest_regression_run = latest_run


def ensure_latest_regression_run() -> LatestRegressionRun | McpOutput:
    if _latest_regression_run is None:
        return McpOutput(reason="No regression run has been executed through this MCP server yet")
    return _latest_regression_run


def get_sarif_message(result: dict[str, Any]) -> str:
    message = result.get("message")
    if isinstance(message, dict):
        text = message.get("text")
        if isinstance(text, str):
            return text
    if isinstance(message, str):
        return message
    return ""


def uri_to_repo_path(uri: str | None) -> str | None:
    if not uri:
        return None

    parsed = urlparse(uri)
    if parsed.scheme == "file":
        file_path = Path(unquote(parsed.path))
    else:
        file_path = Path(uri)

    try:
        return file_path.resolve(strict=False).relative_to(REPO_ROOT).as_posix()
    except ValueError:
        return file_path.as_posix()


def get_sarif_location(result: dict[str, Any]) -> tuple[str | None, int | None, int | None, int | None, int | None]:
    locations = result.get("locations")
    if not isinstance(locations, list) or not locations:
        return None, None, None, None, None

    first = locations[0]
    if not isinstance(first, dict):
        return None, None, None, None, None

    result_file = first.get("resultFile")
    region = None
    file_uri = None
    if isinstance(result_file, dict):
        file_uri = result_file.get("uri")
        region = result_file.get("region")

    if region is None:
        physical_location = first.get("physicalLocation")
        if isinstance(physical_location, dict):
            artifact_location = physical_location.get("artifactLocation")
            if isinstance(artifact_location, dict):
                file_uri = artifact_location.get("uri")
            region = physical_location.get("region")

    if not isinstance(region, dict):
        return uri_to_repo_path(file_uri), None, None, None, None

    return (
        uri_to_repo_path(file_uri),
        region.get("startLine"),
        region.get("startColumn"),
        region.get("endLine"),
        region.get("endColumn"),
    )


def parse_build_summary_counts(output: str) -> tuple[int, int]:
    warning_match = re.search(r"^\s*(\d+)\s+Warning\(s\)", output, re.MULTILINE)
    error_match = re.search(r"^\s*(\d+)\s+Error\(s\)", output, re.MULTILINE)
    warning_count = int(warning_match.group(1)) if warning_match is not None else 0
    error_count = int(error_match.group(1)) if error_match is not None else 0
    return warning_count, error_count


@mcp.tool()
def compile_file(params: CompileParameter) -> CompilerOutput | McpOutput:
    """
    Compiles a file with the blade compiler (Debug build).
    """
    try:
        input_file = map_path(params.file_path)
        if input_file is None:
            return McpOutput(
                reason=f"{params.file_path!r} is a path outside the repository"
            )
        if not input_file.is_file():
            return McpOutput(reason=f"{params.file_path!r} does not exist")

        if contains_param(params.arguments, "--output"):
            return McpOutput(reason="--output cannot be passed through additional arguments")
        if contains_param(params.arguments, "--module"):
            return McpOutput(reason="--module cannot be passed through additional arguments")

        argv: list[str] = [
            str(COMPILER_PATH),
            "--json",
            "--output",
            "-",
        ]

        if params.metrics:
            argv.append("--metrics")

        for name, raw_path in sorted(params.modules.items()):
            path = map_path(raw_path)
            if path is None:
                return McpOutput(
                    reason=f"module {name!r}: {raw_path!r} is a path outside the repository"
                )
            if not path.is_file():
                return McpOutput(reason=f"module {name!r}:{raw_path!r} does not exist")
            argv.append(f"--module={name}={path.as_posix()}")

        if params.dumps is True:
            argv.append("--dump-all")
        elif params.dumps is not False:
            if RequestedDump.bound in params.dumps:
                argv.append("--dump-bound")
            if RequestedDump.mir_preopt in params.dumps:
                argv.append("--dump-mir-preopt")
            if RequestedDump.mir in params.dumps:
                argv.append("--dump-mir")
            if RequestedDump.lir_preopt in params.dumps:
                argv.append("--dump-lir-preopt")
            if RequestedDump.lir in params.dumps:
                argv.append("--dump-lir")
            if RequestedDump.asmir_preopt in params.dumps:
                argv.append("--dump-asmir-preopt")
            if RequestedDump.asmir in params.dumps:
                argv.append("--dump-asmir")
            if RequestedDump.final_asm in params.dumps:
                argv.append("--dump-final-asm")

        for arg in params.arguments:
            mapped = map_argument(arg)
            if isinstance(mapped, McpOutput):
                return mapped
            argv.append(mapped)

        argv.append(str(input_file))
        completed = run_command(argv)

        try:
            compiler_out = RawCompilerResult.model_validate_json(completed.stdout)
        except ValidationError as err:
            stderr = completed.stderr.strip()
            if stderr:
                return McpOutput(reason="Compiler invocation failed", exception=stderr)
            return McpOutput(reason="Unexpected compiler output", exception=str(err))

        if completed.returncode != 0:
            compiler_out.success = False

        dumps: CompilerDumps | None = compiler_out.dumps
        if params.dumps is True:
            dumps.final_asm = compiler_out.result
        elif params.dumps is False:
            dumps = None
        else:
            if RequestedDump.bound not in params.dumps:
                dumps.bound = None
            if RequestedDump.mir_preopt not in params.dumps:
                dumps.mir_preopt = None
            if RequestedDump.mir not in params.dumps:
                dumps.mir = None
            if RequestedDump.lir_preopt not in params.dumps:
                dumps.lir_preopt = None
            if RequestedDump.lir not in params.dumps:
                dumps.lir = None
            if RequestedDump.asmir_preopt not in params.dumps:
                dumps.asmir_preopt = None
            if RequestedDump.asmir not in params.dumps:
                dumps.asmir = None
            dumps.final_asm = compiler_out.result if RequestedDump.final_asm in params.dumps else None

        return CompilerOutput(
            success=compiler_out.success,
            diagnostics=[fmt_diag(diag) for diag in compiler_out.diagnostics],
            result=compiler_out.result if params.result else None,
            dumps=dumps,
            metrics=compiler_out.metrics if params.metrics else None,
        )
    except Exception as err:
        return McpOutput(reason="Unhandled exception in MCP server", exception=str(err))


@mcp.tool()
def is_hw_testrunner_available() -> bool:
    """
    Returns true if .blade_mcp.json configures a serial port and that device path exists.
    """
    hardware_available, _ = get_hw_availability()
    return hardware_available


@mcp.tool()
def execute_regression_fixture(
    file_path: str,
    with_hardware: bool = True,
) -> RegressionFixtureOutput | McpOutput:
    """
    Executes a single regression fixture through Blade.Regressions.
    """
    try:
        fixture_path = map_path(file_path)
        if fixture_path is None:
            return McpOutput(reason=f"{file_path!r} is a path outside the repository")
        if not fixture_path.is_file():
            return McpOutput(reason=f"{file_path!r} does not exist")

        relative_path = repo_relative_path(fixture_path)
        parsed = run_regressions([relative_path], with_hardware)
        if isinstance(parsed, McpOutput):
            return parsed

        _, latest_run = parsed
        update_latest_regression_run(latest_run)
        fixture_result = select_fixture_result(latest_run, relative_path)
        if fixture_result is None:
            return McpOutput(reason=f"No regression result was returned for {relative_path!r}")

        return fixture_result
    except Exception as err:
        return McpOutput(reason="Unhandled exception in MCP server", exception=str(err))


@mcp.tool()
def execute_regression_tests(
    filters: list[str] | None = None,
    with_hardware: bool = True,
) -> RegressionSuiteOutput | McpOutput:
    """
    Executes the regression harness and returns structured failures.
    """
    try:
        parsed = run_regressions(filters or [], with_hardware)
        if isinstance(parsed, McpOutput):
            return parsed

        suite_output, latest_run = parsed
        update_latest_regression_run(latest_run)
        return suite_output
    except Exception as err:
        return McpOutput(reason="Unhandled exception in MCP server", exception=str(err))


@mcp.tool()
def get_regression_result(failed_path: str) -> RegressionFixtureOutput | McpOutput:
    """
    Returns the cached regression result for a path from the latest MCP-triggered run.
    """
    try:
        latest_run = ensure_latest_regression_run()
        if isinstance(latest_run, McpOutput):
            return latest_run

        fixture_result = latest_run.fixture_results.get(failed_path)
        if fixture_result is None:
            return McpOutput(reason=f"No cached regression result exists for {failed_path!r}")
        return fixture_result
    except Exception as err:
        return McpOutput(reason="Unhandled exception in MCP server", exception=str(err))


@mcp.tool()
def get_regression_artifact(
    failed_path: str,
    artifact: str,
) -> RegressionArtifactContent | McpOutput:
    """
    Returns the requested artifact contents for a failed path from the latest cached run.
    """
    try:
        latest_run = ensure_latest_regression_run()
        if isinstance(latest_run, McpOutput):
            return latest_run

        fixture_result = latest_run.fixture_results.get(failed_path)
        if fixture_result is None:
            return McpOutput(reason=f"No cached regression result exists for {failed_path!r}")
        if fixture_result.artifact_directory is None:
            return McpOutput(reason=f"No artifacts are available for {failed_path!r}")

        file_name = ARTIFACT_NAME_TO_FILE.get(artifact)
        if file_name is None:
            return McpOutput(
                reason=f"Unknown artifact {artifact!r}. Available artifacts are: {', '.join(fixture_result.artifact_names)}"
            )

        artifact_path = Path(fixture_result.artifact_directory) / file_name
        if not artifact_path.is_file():
            return McpOutput(reason=f"Artifact {artifact!r} is not available for {failed_path!r}")

        if artifact == "hardware-bin":
            content = artifact_path.read_bytes()
            return RegressionArtifactContent(
                relative_path=failed_path,
                artifact=artifact,
                kind="binary",
                base64_data=base64.b64encode(content).decode("ascii"),
                byte_count=len(content),
            )

        return RegressionArtifactContent(
            relative_path=failed_path,
            artifact=artifact,
            kind="text",
            text=artifact_path.read_text(encoding="utf-8"),
        )
    except Exception as err:
        return McpOutput(reason="Unhandled exception in MCP server", exception=str(err))


@mcp.tool()
def get_instruction_forms(mnemonic: str) -> list[str]:
    """
    Returns the known instruction forms for a mnemonic.
    """
    index = get_instruction_index()
    return index["forms_by_mnemonic"].get(mnemonic.strip().upper(), [])


@mcp.tool()
def get_instruction_form(form: str) -> InstructionFormData | McpOutput:
    """
    Returns metadata for a canonical instruction form.
    """
    try:
        index = get_instruction_index()
        key = canonical_instruction_form_key(form)
        result = index["forms_by_key"].get(key)
        if result is None:
            return McpOutput(reason=f"Unknown instruction form {form!r}")
        return result
    except Exception as err:
        return McpOutput(reason="Unhandled exception in MCP server", exception=str(err))


@mcp.tool()
def get_fuzzy_instruction(query: str) -> list[str]:
    """
    Searches mnemonics and descriptions for relevant instruction forms.
    """
    normalized_query = normalize_text(query).upper()
    if not normalized_query:
        return []

    index = get_instruction_index()
    scored: list[tuple[int, str]] = []
    seen: set[str] = set()

    for syntax, mnemonic, description in index["search_rows"]:
        haystack_syntax = syntax.upper()
        haystack_description = description.upper()
        query_tokens = [token for token in re.split(r"\s+", normalized_query) if token]

        score = 0
        if normalized_query == mnemonic:
            score += 100
        elif mnemonic.startswith(normalized_query):
            score += 80
        elif normalized_query in mnemonic:
            score += 60

        if normalized_query == haystack_syntax:
            score += 95
        elif normalized_query in haystack_syntax:
            score += 70

        if normalized_query in haystack_description:
            score += 50
        score += 15 * sum(1 for token in query_tokens if token in haystack_description)
        score += 10 * sum(1 for token in query_tokens if token in haystack_syntax)

        if score == 0:
            continue

        score += int(
            20
            * difflib.SequenceMatcher(
                None,
                normalized_query,
                haystack_syntax,
            ).ratio()
        )
        if syntax not in seen:
            scored.append((score, syntax))
            seen.add(syntax)

    scored.sort(key=lambda item: (-item[0], item[1]))
    return [syntax for _, syntax in scored[:20]]


@mcp.tool()
def build_compiler() -> BuildCompilerOutput | McpOutput:
    """
    Runs dotnet build --no-restore for Blade/blade.csproj and returns warning/error diagnostics from SARIF.
    """
    sarif_path: Path | None = None
    try:
        file_descriptor, temp_path = tempfile.mkstemp(suffix=".sarif")
        os.close(file_descriptor)
        sarif_path = Path(temp_path)
        sarif_path.unlink(missing_ok=True)

        build_argv = [
            "dotnet",
            "build",
            "--no-restore",
            str(COMPILER_PROJECT_PATH),
            f"-p:ErrorLog={sarif_path.as_posix()},version=2.1",
        ]
        completed = run_command(build_argv)

        if not sarif_path.is_file():
            python3_path = Path("/usr/bin/python3")
            if python3_path.is_file():
                helper = (
                    "import subprocess, sys\n"
                    "result = subprocess.run(sys.argv[1:], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8')\n"
                    "sys.stdout.write(result.stdout)\n"
                    "sys.stderr.write(result.stderr)\n"
                    "raise SystemExit(result.returncode)\n"
                )
                completed = subprocess.run(
                    [str(python3_path), "-c", helper, *build_argv],
                    stdin=subprocess.DEVNULL,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    cwd=REPO_ROOT,
                    check=False,
                    encoding="utf-8",
                )

        if not sarif_path.is_file():
            detail = completed.stderr.strip() or completed.stdout.strip()
            warning_count, error_count = parse_build_summary_counts(detail)
            diagnostics: list[BuildDiagnostic] = []
            if completed.returncode != 0 or warning_count > 0 or error_count > 0:
                severity: Literal["warning", "error"] = "error" if completed.returncode != 0 or error_count > 0 else "warning"
                diagnostics.append(
                    BuildDiagnostic(
                        severity=severity,
                        code="BUILD",
                        message=detail or "dotnet build did not produce a SARIF file",
                        file=None,
                        line=None,
                        column=None,
                    )
                )
            return BuildCompilerOutput(
                success=completed.returncode == 0 and error_count == 0,
                warning_count=warning_count,
                error_count=error_count,
                diagnostics=diagnostics,
            )

        sarif_text = sarif_path.read_text(encoding="utf-8")
        if not sarif_text.strip():
            detail = completed.stderr.strip() or completed.stdout.strip()
            warning_count, error_count = parse_build_summary_counts(detail)
            diagnostics: list[BuildDiagnostic] = []
            if completed.returncode != 0 or warning_count > 0 or error_count > 0:
                severity = "error" if completed.returncode != 0 or error_count > 0 else "warning"
                diagnostics.append(
                    BuildDiagnostic(
                        severity=severity,
                        code="BUILD",
                        message=detail or "dotnet build produced an empty SARIF file",
                        file=None,
                        line=None,
                        column=None,
                    )
                )
            return BuildCompilerOutput(
                success=completed.returncode == 0 and error_count == 0,
                warning_count=warning_count,
                error_count=error_count,
                diagnostics=diagnostics,
            )

        sarif = json.loads(sarif_text)
        diagnostics: list[BuildDiagnostic] = []
        for run in sarif.get("runs", []):
            if not isinstance(run, dict):
                continue
            for result in run.get("results", []):
                if not isinstance(result, dict):
                    continue
                level = str(result.get("level", "")).lower()
                if level not in ("warning", "error"):
                    continue

                file, line, column, end_line, end_column = get_sarif_location(result)
                diagnostics.append(
                    BuildDiagnostic(
                        severity=level,
                        code=str(result.get("ruleId", "")) or "BUILD",
                        message=get_sarif_message(result),
                        file=file,
                        line=line,
                        column=column,
                        end_line=end_line,
                        end_column=end_column,
                    )
                )

        if completed.returncode != 0 and not diagnostics:
            diagnostics.append(
                BuildDiagnostic(
                    severity="error",
                    code="BUILD",
                    message=(completed.stderr.strip() or completed.stdout.strip() or "dotnet build failed"),
                    file=None,
                    line=None,
                    column=None,
                )
            )

        warning_count = sum(1 for item in diagnostics if item.severity == "warning")
        error_count = sum(1 for item in diagnostics if item.severity == "error")
        return BuildCompilerOutput(
            success=completed.returncode == 0 and error_count == 0,
            warning_count=warning_count,
            error_count=error_count,
            diagnostics=diagnostics,
        )
    except Exception as err:
        return McpOutput(reason="Unhandled exception in MCP server", exception=str(err))
    finally:
        if sarif_path is not None:
            try:
                sarif_path.unlink(missing_ok=True)
            except OSError:
                pass


if __name__ == "__main__":
    mcp.run()
