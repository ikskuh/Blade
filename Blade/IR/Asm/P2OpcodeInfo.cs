using System.Collections.Generic;

namespace Blade.IR.Asm;

/// <summary>
/// Static classification of P2 opcodes for def/use analysis in register allocation.
/// Covers all opcodes emitted by the Blade compiler.
/// </summary>
public static class P2OpcodeInfo
{
    // Opcodes that write to D (operand[0]) without reading it first.
    private static readonly HashSet<string> DefineOnlyOpcodes =
    [
        "MOV", "NEG", "ABS", "NOT",
        "GETQX", "GETQY",
        "WRC", "WRNC", "WRZ", "WRNZ",
        "MUXC", "MUXNC", "MUXZ", "MUXNZ",
        "LOC",
    ];

    // Opcodes that read D, modify it, and write back (read-modify-write).
    private static readonly HashSet<string> ReadModifyWriteOpcodes =
    [
        "ADD", "SUB", "AND", "OR", "XOR",
        "SHL", "SHR", "SAR", "ROL", "ROR",
        "ENCOD", "DECOD", "BMASK", "ZEROX", "SIGNX",
        "BITH", "BITL", "BITNOT", "BITC", "BITNC", "BITZ", "BITNZ", "BITRND",
        "DJNZ", "DJZ",
    ];

    // Opcodes that only read both operands and do not write D.
    private static readonly HashSet<string> ReadOnlyOpcodes =
    [
        "CMP", "CMPS", "CMPR", "CMPX", "CMPSX",
        "TEST", "TESTP", "TESTB", "TESTBN",
        "QMUL", "QDIV", "QFRAC", "QLOG", "QEXP",
    ];

    private static readonly HashSet<string> CallOpcodes =
    [
        "CALL", "CALLA", "CALLB", "CALLD", "CALLPA", "CALLPB",
    ];

    private static readonly HashSet<string> BranchOpcodes =
    [
        "JMP", "TJZ", "TJNZ", "TJF", "TJNF", "DJNZ", "DJZ",
    ];

    private static readonly HashSet<string> ReturnOpcodes =
    [
        "RET", "RETA", "RETB", "RETI0", "RETI1", "RETI2", "RETI3",
    ];

    // Opcodes that have no register effects (directives, NOPs, etc.)
    private static readonly HashSet<string> NoEffectOpcodes =
    [
        "NOP", "REP", "AUGS", "AUGD",
    ];

    private static readonly HashSet<string> SpecialRegisterNames =
    [
        "PA", "PB", "PTRA", "PTRB",
        "DIRA", "DIRB", "OUTA", "OUTB", "INA", "INB",
        "IJMP1", "IRET1", "IJMP2", "IRET2", "IJMP3", "IRET3",
    ];

    private static readonly HashSet<string> PureRegisterLocalOpcodes =
    [
        "MOV", "NEG", "ABS", "NOT",
        "ADD", "SUB", "AND", "OR", "XOR",
        "SHL", "SHR", "SAR", "ROL", "ROR",
        "ENCOD", "DECOD", "BMASK", "ZEROX", "SIGNX",
        "BITH", "BITL", "BITNOT", "BITZ", "BITNZ", "BITRND",
    ];

    /// <summary>
    /// Returns true if the opcode writes to operand[0] (the D field).
    /// For predicated instructions, the caller must treat D as both def and use.
    /// </summary>
    public static bool DefinesDestination(string opcode)
        => DefineOnlyOpcodes.Contains(opcode) || ReadModifyWriteOpcodes.Contains(opcode);

    /// <summary>
    /// Returns true if the opcode reads operand[0] as part of a read-modify-write.
    /// </summary>
    public static bool ReadsDestination(string opcode)
        => ReadModifyWriteOpcodes.Contains(opcode);

    /// <summary>
    /// Returns true if the opcode only reads both operands without writing D.
    /// </summary>
    public static bool IsReadOnly(string opcode)
        => ReadOnlyOpcodes.Contains(opcode);

    /// <summary>
    /// Returns true if the opcode is a call instruction.
    /// </summary>
    public static bool IsCall(string opcode)
        => CallOpcodes.Contains(opcode);

    /// <summary>
    /// Returns true if the opcode is a branch (conditional or unconditional).
    /// Note: DJNZ/DJZ are both branches AND read-modify-write on D.
    /// </summary>
    public static bool IsBranch(string opcode)
        => BranchOpcodes.Contains(opcode);

    /// <summary>
    /// Returns true if the opcode is a return instruction.
    /// </summary>
    public static bool IsReturn(string opcode)
        => ReturnOpcodes.Contains(opcode);

    /// <summary>
    /// Returns true if the opcode affects control flow (branch, call, or return).
    /// </summary>
    public static bool IsControlFlow(string opcode)
        => IsBranch(opcode) || IsCall(opcode) || IsReturn(opcode);

    /// <summary>
    /// Returns true if the opcode has no register read/write effects.
    /// </summary>
    public static bool HasNoRegisterEffect(string opcode)
        => NoEffectOpcodes.Contains(opcode);

    public static bool IsSpecialRegisterName(string name)
        => SpecialRegisterNames.Contains(name);

    /// <summary>
    /// Returns true when the instruction is a local-register-only data transform
    /// that may be removed if its destination is dead.
    /// </summary>
    public static bool IsPureRegisterLocalInstruction(AsmInstructionNode instruction)
    {
        if (instruction.Predicate is not null
            || instruction.FlagEffect != AsmFlagEffect.None
            || instruction.Operands.Count == 0
            || instruction.Operands[0] is not AsmRegisterOperand
            || !PureRegisterLocalOpcodes.Contains(instruction.Opcode))
        {
            return false;
        }

        for (int i = 1; i < instruction.Operands.Count; i++)
        {
            if (instruction.Operands[i] is AsmRegisterOperand or AsmImmediateOperand)
                continue;
            return false;
        }

        return true;
    }
}
