namespace Blade.IR.Lir;

public enum LirValueType
{
    Register,
    Flag,
}

/// <summary>
/// A value threaded through LIR.
/// </summary>
public abstract class VirtualLirValue
{
    public abstract LirValueType Type { get; }
}

/// <summary>
/// A value threaded through LIR that refers to a virtual 32-bit register value.
/// </summary>
public sealed class VirtualLirRegister : VirtualLirValue
{
    public VirtualLirRegister()
    {
    }

    public override LirValueType Type => LirValueType.Register;
}

/// <summary>
/// A value threaded through LIR that refers to a virtual 1-bit flag value.
/// </summary>
public sealed class VirtualLirFlag : VirtualLirValue
{
    public VirtualLirFlag()
    {
    }

    public override LirValueType Type => LirValueType.Flag;
}
