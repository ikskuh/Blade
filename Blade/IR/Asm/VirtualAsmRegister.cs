using System.Runtime.CompilerServices;
using Blade.IR.Lir;

namespace Blade.IR.Asm;

public sealed class VirtualAsmRegister
{
    private static ConditionalWeakTable<VirtualLirRegister, VirtualAsmRegister> _fromLirRegisters = new();

    public VirtualAsmRegister()
    {
    }

    public static VirtualAsmRegister FromLir(VirtualLirRegister register)
    {
        Requires.NotNull(register);
        return _fromLirRegisters.GetValue(register, static _ => new VirtualAsmRegister());
    }

    internal static void ResetMappings()
    {
        _fromLirRegisters = new ConditionalWeakTable<VirtualLirRegister, VirtualAsmRegister>();
    }
}
