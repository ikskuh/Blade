using System.Collections.Generic;
using Blade.IR.Lir;

namespace Blade.IR.Asm;

/// <summary>
/// Maintains the LIR-to-ASM virtual register association for a single lowering session.
/// The mapping is intentionally local to avoid hidden global state across lowers.
/// </summary>
internal sealed class RegisterAssociator
{
    private readonly Dictionary<VirtualLirValue, VirtualAsmValue> _lirToAsm = [];

    public VirtualAsmRegister FromLir(VirtualLirRegister register)
    {
        Requires.NotNull(register);

        if (_lirToAsm.TryGetValue(register, out VirtualAsmValue? mapped))
            return (VirtualAsmRegister)mapped;

        VirtualAsmRegister created = new();
        _lirToAsm.Add(register, created);
        return created;
    }

    public VirtualAsmFlag FromLir(VirtualLirFlag flag)
    {
        Requires.NotNull(flag);

        if (_lirToAsm.TryGetValue(flag, out VirtualAsmValue? mapped))
            return (VirtualAsmFlag)mapped;

        VirtualAsmFlag created = new();
        _lirToAsm.Add(flag, created);
        return created;
    }

    public VirtualAsmValue FromLir(VirtualLirValue value)
    {
        Requires.NotNull(value);

        return value switch
        {
            VirtualLirRegister register => FromLir(register),
            VirtualLirFlag flag => FromLir(flag),
            _ => Assert.UnreachableValue<VirtualAsmValue>(), // pragma: force-coverage
        };
    }
}
