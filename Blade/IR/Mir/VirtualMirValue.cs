namespace Blade.IR.Mir;

public enum MirValueType
{
    Register,
    Flag,
}

/// <summary>
/// A value threaded through MIR.
/// </summary>
public abstract class VirtualMirValue
{
    public abstract MirValueType Type { get; }
}

/// <summary>
/// A value threaded through MIR that refers to a virtual 32-bit register value.
/// </summary>
public sealed class VirtualMirRegister : VirtualMirValue
{
    public VirtualMirRegister()
    {
    }

    public override MirValueType Type => MirValueType.Register;
}

/// <summary>
/// A value threaded through MIR that refers to a virtual 1-bit flag value.
/// </summary>
public sealed class VirtualMirFlag : VirtualMirValue
{
    public VirtualMirFlag()
    {
    }

    public override MirValueType Type => MirValueType.Flag;
}
