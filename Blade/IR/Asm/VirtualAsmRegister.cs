namespace Blade.IR.Asm;

public enum AsmValueType
{
    Register,
    Flag,
}

public abstract class VirtualAsmValue
{
    public abstract AsmValueType Type { get; }
}

public sealed class VirtualAsmRegister : VirtualAsmValue
{
    public VirtualAsmRegister()
    {
    }

    public override AsmValueType Type => AsmValueType.Register;
}

public sealed class VirtualAsmFlag : VirtualAsmValue
{
    public VirtualAsmFlag()
    {
    }

    public override AsmValueType Type => AsmValueType.Flag;
}
