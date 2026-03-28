namespace Blade.IR.Lir;

public sealed class LirBlockRef
{
    public LirBlockRef(string? debugName = null)
    {
        DebugName = debugName;
    }

    public string? DebugName { get; set; }

    public override string ToString()
        => DebugName ?? "<anon>";
}
