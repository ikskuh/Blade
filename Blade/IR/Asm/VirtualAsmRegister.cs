using System.Threading;
using System.Runtime.CompilerServices;
using Blade.IR.Lir;

namespace Blade.IR.Asm;

public class VirtualAsmRegister
{
    private static int _nextDebugId;
    private static ConditionalWeakTable<VirtualLirRegister, VirtualAsmRegister> _fromLirRegisters = new();

    public VirtualAsmRegister()
    {
        DebugId = Interlocked.Increment(ref _nextDebugId) - 1;
    }

    public VirtualAsmRegister(int debugId)
    {
        DebugId = debugId;
    }

    public int DebugId { get; }
    public int Id => DebugId;
    public VirtualAsmRegister Value => this;

    public static VirtualAsmRegister FromLir(VirtualLirRegister register)
    {
        Requires.NotNull(register);
        return _fromLirRegisters.GetValue(register, static lirRegister => new VirtualAsmRegister(lirRegister.DebugId));
    }

    internal static void ResetDebugIds()
    {
        Interlocked.Exchange(ref _nextDebugId, 0);
        _fromLirRegisters = new ConditionalWeakTable<VirtualLirRegister, VirtualAsmRegister>();
    }

    public override string ToString() => $"%r{DebugId}";
}
