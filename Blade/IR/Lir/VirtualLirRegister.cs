using System.Threading;

namespace Blade.IR.Lir;

public class VirtualLirRegister
{
    private static int _nextDebugId;

    public VirtualLirRegister()
    {
        DebugId = Interlocked.Increment(ref _nextDebugId) - 1;
    }

    public VirtualLirRegister(int debugId)
    {
        DebugId = debugId;
    }

    public int DebugId { get; }
    public VirtualLirRegister Value => this;

    internal static void ResetDebugIds()
    {
        Interlocked.Exchange(ref _nextDebugId, 0);
    }

    public override string ToString() => $"%r{DebugId}";
}
