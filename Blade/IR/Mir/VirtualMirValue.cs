using System.Threading;

namespace Blade.IR.Mir;

public class VirtualMirValue
{
    private static int _nextDebugId;

    public VirtualMirValue()
    {
        DebugId = Interlocked.Increment(ref _nextDebugId) - 1;
    }

    public VirtualMirValue(int debugId)
    {
        DebugId = debugId;
    }

    public int DebugId { get; }
    public int Id => DebugId;
    public VirtualMirValue Value => this;

    internal static void ResetDebugIds()
    {
        Interlocked.Exchange(ref _nextDebugId, 0);
    }

    public override string ToString() => $"%v{DebugId}";
}
