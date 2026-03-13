using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Semantics;

/// <summary>
/// Validates inline assembly blocks against the Propeller 2 instruction set.
/// Parses asm body text into structured lines, validates instruction mnemonics,
/// condition prefixes, flag effects, and variable references.
/// </summary>
public static class InlineAssemblyValidator
{
    /// <summary>
    /// Complete set of valid P2 instruction mnemonics (including aliases).
    /// Derived from "Parallax Propeller 2 Instructions v35 - Rev B_C Silicon.csv".
    /// </summary>
    private static readonly FrozenSet<string> ValidInstructions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ABS", "ADD", "ADDCT1", "ADDCT2", "ADDCT3", "ADDPIX", "ADDS", "ADDSX", "ADDX",
        "AKPIN", "ALLOWI", "ALTB", "ALTD", "ALTGB", "ALTGN", "ALTGW", "ALTI", "ALTR",
        "ALTS", "ALTSB", "ALTSN", "ALTSW", "AND", "ANDN", "AUGD", "AUGS",
        "BITC", "BITH", "BITL", "BITNC", "BITNOT", "BITNZ", "BITRND", "BITZ",
        "BLNPIX", "BMASK", "BRK",
        "CALL", "CALLA", "CALLB", "CALLD", "CALLPA", "CALLPB",
        "CMP", "CMPM", "CMPR", "CMPS", "CMPSUB", "CMPSX", "CMPX",
        "COGATN", "COGBRK", "COGID", "COGINIT", "COGSTOP",
        "CRCBIT", "CRCNIB",
        "DECMOD", "DECOD",
        "DIRC", "DIRH", "DIRL", "DIRNC", "DIRNOT", "DIRNZ", "DIRRND", "DIRZ",
        "DJF", "DJNF", "DJNZ", "DJZ",
        "DRVC", "DRVH", "DRVL", "DRVNC", "DRVNOT", "DRVNZ", "DRVRND", "DRVZ",
        "ENCOD", "EXECF",
        "FBLOCK", "FGE", "FGES", "FLE", "FLES",
        "FLTC", "FLTH", "FLTL", "FLTNC", "FLTNOT", "FLTNZ", "FLTRND", "FLTZ",
        "GETBRK", "GETBYTE", "GETCT", "GETNIB", "GETPTR", "GETQX", "GETQY",
        "GETRND", "GETSCP", "GETWORD", "GETXACC",
        "HUBSET",
        "IJNZ", "IJZ", "INCMOD",
        "JATN", "JCT1", "JCT2", "JCT3", "JFBW", "JINT", "JMP", "JMPREL",
        "JNATN", "JNCT1", "JNCT2", "JNCT3", "JNFBW", "JNINT", "JNPAT", "JNQMT",
        "JNSE1", "JNSE2", "JNSE3", "JNSE4", "JNXFI", "JNXMT", "JNXRL", "JNXRO",
        "JPAT", "JQMT", "JSE1", "JSE2", "JSE3", "JSE4",
        "JXFI", "JXMT", "JXRL", "JXRO",
        "LOC", "LOCKNEW", "LOCKREL", "LOCKRET", "LOCKTRY",
        "MERGEB", "MERGEW", "MIXPIX", "MODC", "MODCZ", "MODZ",
        "MOV", "MOVBYTS", "MUL", "MULPIX", "MULS",
        "MUXC", "MUXNC", "MUXNIBS", "MUXNITS", "MUXNZ", "MUXQ", "MUXZ",
        "NEG", "NEGC", "NEGNC", "NEGNZ", "NEGZ",
        "NIXINT1", "NIXINT2", "NIXINT3", "NOP", "NOT",
        "ONES", "OR",
        "OUTC", "OUTH", "OUTL", "OUTNC", "OUTNOT", "OUTNZ", "OUTRND", "OUTZ",
        "POLLATN", "POLLCT1", "POLLCT2", "POLLCT3", "POLLFBW", "POLLINT",
        "POLLPAT", "POLLQMT", "POLLSE1", "POLLSE2", "POLLSE3", "POLLSE4",
        "POLLXFI", "POLLXMT", "POLLXRL", "POLLXRO",
        "POP", "POPA", "POPB", "PUSH", "PUSHA", "PUSHB",
        "QDIV", "QEXP", "QFRAC", "QLOG", "QMUL", "QROTATE", "QSQRT", "QVECTOR",
        "RCZL", "RCZR",
        "RDBYTE", "RDFAST", "RDLONG", "RDLUT", "RDPIN", "RDWORD",
        "REP",
        "RESI0", "RESI1", "RESI2", "RESI3",
        "RET", "RETA", "RETB", "RETI0", "RETI1", "RETI2", "RETI3",
        "REV",
        "RFBYTE", "RFLONG", "RFVAR", "RFVARS", "RFWORD",
        "RGBEXP", "RGBSQZ",
        "ROL", "ROLBYTE", "ROLNIB", "ROLWORD", "ROR",
        "RQPIN",
        "RCL", "RCR",
        "SAL", "SAR",
        "SCA", "SCAS",
        "SETBYTE", "SETCFRQ", "SETCI", "SETCMOD", "SETCQ", "SETCY",
        "SETD", "SETDACS", "SETINT1", "SETINT2", "SETINT3", "SETLUTS",
        "SETNIB", "SETPAT", "SETPIV", "SETPIX", "SETQ", "SETQ2",
        "SETR", "SETS", "SETSCP", "SETSE1", "SETSE2", "SETSE3", "SETSE4",
        "SETWORD", "SETXFRQ",
        "SEUSSF", "SEUSSR",
        "SHL", "SHR", "SIGNX",
        "SKIP", "SKIPF", "SPLITB", "SPLITW", "STALLI",
        "SUB", "SUBR", "SUBS", "SUBSX", "SUBX",
        "SUMC", "SUMNC", "SUMNZ", "SUMZ",
        "TEST", "TESTB", "TESTBN", "TESTN", "TESTP", "TESTPN",
        "TJF", "TJNF", "TJNS", "TJNZ", "TJS", "TJV", "TJZ",
        "TRGINT1", "TRGINT2", "TRGINT3",
        "WAITATN", "WAITCT1", "WAITCT2", "WAITCT3", "WAITFBW", "WAITINT",
        "WAITPAT", "WAITSE1", "WAITSE2", "WAITSE3", "WAITSE4",
        "WAITX", "WAITXFI", "WAITXMT", "WAITXRL", "WAITXRO",
        "WFBYTE", "WFLONG", "WFWORD",
        "WMLONG", "WRBYTE", "WRC", "WRFAST", "WRLONG", "WRLUT",
        "WRNC", "WRNZ", "WRPIN", "WRWORD", "WRZ",
        "WXPIN", "WYPIN",
        "XCONT", "XINIT", "XOR", "XORO32", "XSTOP", "XZERO",
        "ZEROX",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Valid condition prefixes (IF_xx and _RET_).
    /// </summary>
    private static readonly FrozenSet<string> ValidConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "_RET_",
        "IF_NC_AND_NZ", "IF_NZ_AND_NC", "IF_GT", "IF_A", "IF_00",
        "IF_NC_AND_Z", "IF_Z_AND_NC", "IF_01",
        "IF_NC", "IF_GE", "IF_AE", "IF_0X",
        "IF_C_AND_NZ", "IF_NZ_AND_C", "IF_10",
        "IF_NZ", "IF_NE", "IF_X0",
        "IF_C_NE_Z", "IF_Z_NE_C", "IF_DIFF",
        "IF_NC_OR_NZ", "IF_NZ_OR_NC", "IF_NOT_11",
        "IF_C_AND_Z", "IF_Z_AND_C", "IF_11",
        "IF_C_EQ_Z", "IF_Z_EQ_C", "IF_SAME",
        "IF_Z", "IF_E", "IF_X1",
        "IF_NC_OR_Z", "IF_Z_OR_NC", "IF_NOT_10",
        "IF_C", "IF_LT", "IF_B", "IF_1X",
        "IF_C_OR_NZ", "IF_NZ_OR_C", "IF_NOT_01",
        "IF_C_OR_Z", "IF_Z_OR_C", "IF_LE", "IF_BE", "IF_NOT_00",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Valid flag effect suffixes.
    /// </summary>
    private static readonly FrozenSet<string> ValidFlagEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WC", "WZ", "WCZ",
        "ANDC", "ANDZ",
        "ORC", "ORZ",
        "XORC", "XORZ",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Represents a single parsed inline assembly instruction line.
    /// </summary>
    public sealed class AsmLine
    {
        public string? Condition { get; init; }
        public string Mnemonic { get; init; } = "";
        public string[] Operands { get; init; } = [];
        public string? FlagEffect { get; init; }
        public string RawText { get; init; } = "";
    }

    /// <summary>
    /// Result of validating an inline assembly block.
    /// </summary>
    public sealed class ValidationResult
    {
        public List<AsmLine> Lines { get; } = [];
        public List<string> ReferencedVariables { get; } = [];
        public bool IsValid { get; set; } = true;
    }

    /// <summary>
    /// Validates the body of an asm { ... } block.
    /// Reports diagnostics for unknown instructions, bad variable references, etc.
    /// Returns structured parse result for downstream codegen.
    /// </summary>
    public static ValidationResult Validate(
        string body,
        TextSpan blockSpan,
        HashSet<string> availableVariables,
        DiagnosticBag diagnostics)
    {
        ValidationResult result = new();
        string[] rawLines = body.Split('\n');

        foreach (string rawLine in rawLines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Strip trailing comments
            int commentIdx = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0)
                trimmed = trimmed[..commentIdx].Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            AsmLine? line = ParseAsmLine(trimmed, blockSpan, availableVariables, diagnostics, result);
            if (line is not null)
                result.Lines.Add(line);
            else
                result.IsValid = false;
        }

        return result;
    }

    private static AsmLine? ParseAsmLine(
        string text,
        TextSpan blockSpan,
        HashSet<string> availableVariables,
        DiagnosticBag diagnostics,
        ValidationResult result)
    {
        // Tokenize: split on whitespace, but respect {var} references and commas
        // Format: [CONDITION] MNEMONIC [operands...] [WC/WZ/WCZ]
        // Operands may contain {varname}, #immediate, register names

        string remaining = text;
        string? condition = null;
        string? flagEffect = null;

        // Check for condition prefix at the start
        string firstWord = GetFirstWord(remaining);
        if (ValidConditions.Contains(firstWord))
        {
            condition = firstWord.ToUpperInvariant();
            remaining = remaining[firstWord.Length..].TrimStart();
        }

        // Now extract the mnemonic
        string mnemonic = GetFirstWord(remaining);
        if (string.IsNullOrEmpty(mnemonic))
        {
            diagnostics.ReportInlineAsmEmptyInstruction(blockSpan);
            return null;
        }

        if (!ValidInstructions.Contains(mnemonic))
        {
            diagnostics.ReportInlineAsmUnknownInstruction(blockSpan, mnemonic);
            return null;
        }

        remaining = remaining[mnemonic.Length..].TrimStart();

        // Check for flag effects at the end
        // Need to look at the last word(s)
        string lastWord = GetLastWord(remaining);
        if (!string.IsNullOrEmpty(lastWord) && ValidFlagEffects.Contains(lastWord))
        {
            flagEffect = lastWord.ToUpperInvariant();
            remaining = remaining[..remaining.LastIndexOf(lastWord, StringComparison.OrdinalIgnoreCase)].TrimEnd();
        }

        // Parse operands (comma-separated, may contain {varname} or # immediates)
        List<string> operands = [];
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            // Split by commas, trim each
            string[] parts = remaining.Split(',');
            foreach (string part in parts)
            {
                string op = part.Trim();
                if (!string.IsNullOrEmpty(op))
                    operands.Add(op);
            }
        }

        // Validate variable references in operands
        foreach (string operand in operands)
        {
            // Find all {varname} references
            MatchCollection matches = Regex.Matches(operand, @"\{(\w+)\}");
            foreach (Match match in matches)
            {
                string varName = match.Groups[1].Value;
                if (!availableVariables.Contains(varName))
                {
                    diagnostics.ReportInlineAsmUndefinedVariable(blockSpan, varName);
                    result.IsValid = false;
                }
                else if (!result.ReferencedVariables.Contains(varName))
                {
                    result.ReferencedVariables.Add(varName);
                }
            }
        }

        return new AsmLine
        {
            Condition = condition,
            Mnemonic = mnemonic.ToUpperInvariant(),
            Operands = operands.ToArray(),
            FlagEffect = flagEffect,
            RawText = text,
        };
    }

    private static string GetFirstWord(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ',')
            i++;
        return text[..i];
    }

    private static string GetLastWord(string text)
    {
        string trimmed = text.TrimEnd();
        if (string.IsNullOrEmpty(trimmed))
            return "";
        int i = trimmed.Length - 1;
        while (i >= 0 && !char.IsWhiteSpace(trimmed[i]) && trimmed[i] != ',')
            i--;
        return trimmed[(i + 1)..];
    }

    /// <summary>
    /// Returns true if the given mnemonic is a valid P2 instruction.
    /// </summary>
    public static bool IsValidInstruction(string mnemonic)
        => ValidInstructions.Contains(mnemonic);

    /// <summary>
    /// Returns true if the given name is a valid condition prefix.
    /// </summary>
    public static bool IsValidCondition(string name)
        => ValidConditions.Contains(name);
}
