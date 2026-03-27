using System.Threading;

namespace Blade.IR.Asm;

public class VirtualAsmRegister
{
    private static int _nextDebugId;

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

    internal static void ResetDebugIds()
    {
        Interlocked.Exchange(ref _nextDebugId, 0);
    }

    public override string ToString() => $"%r{DebugId}";
}
