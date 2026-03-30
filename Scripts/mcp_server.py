#!/usr/bin/env python3
from enum import Enum
import json
import os
import subprocess

from pathlib import Path
from typing import Any, Iterable, Literal

from pydantic import BaseModel, Field, ValidationError

from mcp.server.fastmcp import FastMCP

REPO_ROOT = Path(__file__).parent.parent

COMPILER_PATH = REPO_ROOT / "Blade/bin/Debug/net10.0/blade"

# TODO: Implement module and opt selection in the MCP server.


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
        description="Set of the requested dumps internal state dumps or a boolean if all dumps should be included in the result",
        default=False,
    )

    metrics: bool = Field(
        description="Should the output contain the compiler metrics (token count, member count, ...)",
    )

    result: bool = Field(
        description="Should the output include the generated code",
    )

    arguments: list[str] = Field(
        description="Additional command line arguments passed to the compiler",
        default_factory=list,
    )

    modules: dict[str, str] = Field(
        description="A collection of modules. The key is the module name, the associated value is the path to the module file. Paths are relative to repository root.",
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
    success: bool = Field(description="The execution was a success")
    diagnostics: list[str] = Field(description="List if diagnostic messages")
    dumps: CompilerDumps | None = Field(description="The available compiler dumps")
    result: str | None = Field(description="The final generated assembler code")
    metrics: Metrics | None = Field(description="The compiler metrics")


class McpOutput(BaseModel):
    success: Literal[False] = Field(
        description="The execution was a success",
        default=False,
    )
    reason: str = Field(description="The reason why the MCP server crashed")
    exception: str | None = Field(
        default=None, description="The exception dump, if available"
    )


def fmt_diag(diag: Diagnostic) -> str:
    path = (
        Path(os.path.normpath((REPO_ROOT / diag.file).absolute()))
        .relative_to(REPO_ROOT)
        .as_posix()
    )

    return f"{path}:{diag.line}: {diag.code}: {diag.message}"


mcp = FastMCP("TestExecutionServer")


def map_path(raw_path: str) -> Path | None:
    "Maps a path from an unvalidated raw path into a repo-relative path."

    path = (REPO_ROOT / raw_path).resolve(strict=False)
    if path != REPO_ROOT and REPO_ROOT not in path.parents:
        return None
    return path

def contains_param(items: Iterable[str], name: str) -> bool:
    "Checks if 'items' contains 'name' or 'name=…'"

    for item in items:
        if item == name:
            return True 
        if item.startswith(name + "="):
            return True
        
    return False 

def map_argument(arg: str) -> str | McpOutput:
    "Maps connector-specific path-bearing compiler arguments."

    if not arg.startswith("--runtime="):
        return arg

    raw_path = arg[len("--runtime="):]
    path = map_path(raw_path)
    if path is None:
        return McpOutput(
            reason=f"runtime template {raw_path!r} is a path outside the repository"
        )
    if not path.is_file():
        return McpOutput(reason=f"runtime template {raw_path!r} does not exist")

    return f"--runtime={path.as_posix()}"

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

        if contains_param(params.arguments,  "--output"):
            return McpOutput(
                reason="--output cannot be passed through additional arguments"
            )

        if contains_param(params.arguments, "--module"):
            return McpOutput(
                reason="--module cannot be passed through additional arguments"
            )

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

        result = subprocess.run(
            argv,
            executable=COMPILER_PATH,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=REPO_ROOT,
            check=False,
            encoding="utf-8",
        )

        compiler_out: RawCompilerResult
        try:
            compiler_out = RawCompilerResult.model_validate_json(result.stdout)
        except ValidationError as err:
            stderr = result.stderr.strip()
            if stderr:
                return McpOutput(reason="Compiler invocation failed", exception=stderr)

            return McpOutput(reason="Unexpected compiler output", exception=str(err))

        if result.returncode != 0:
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

            if RequestedDump.final_asm in params.dumps:
                dumps.final_asm = compiler_out.result
            else:
                dumps.final_asm = None

        return CompilerOutput(
            success=compiler_out.success,
            diagnostics=[fmt_diag(diag) for diag in compiler_out.diagnostics],
            result=compiler_out.result if params.result else None,
            dumps=dumps,
            metrics=compiler_out.metrics if params.metrics else None,
        )

    except Exception as e:
        return McpOutput(reason="Unhandled exception in MCP server", exception=str(e))


if __name__ == "__main__":
    mcp.run()
