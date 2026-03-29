using System.Collections.Generic;
using Blade.IR.Lir;

namespace Blade.IR.Asm;

/// <summary>
/// Maintains the LIR-to-ASM virtual register association for a single lowering session.
/// The mapping is intentionally local to avoid hidden global state across lowers.
/// </summary>
internal sealed class RegisterAssociator
{
    private readonly Dictionary<VirtualLirRegister, VirtualAsmRegister> _lirToAsm = [];

    public VirtualAsmRegister FromLir(VirtualLirRegister register)
    {
        Requires.NotNull(register);

        if (_lirToAsm.TryGetValue(register, out VirtualAsmRegister? mapped))
            return mapped;

        VirtualAsmRegister created = new();
        _lirToAsm.Add(register, created);
        return created;
    }
}
