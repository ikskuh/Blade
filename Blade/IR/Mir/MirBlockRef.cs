namespace Blade.IR.Mir;

public sealed class MirBlockRef
{
    public MirBlockRef(string? debugName = null)
    {
        DebugName = debugName;
    }

    public string? DebugName { get; set; }

    public override string ToString()
        => DebugName ?? "<anon>";
}
