namespace Blade.IR;

public sealed class EmitOptions
{
    public bool EnableAsmOptimization { get; init; } = true;
    public bool EnableRegisterAllocation { get; init; } = true;
    public bool EnableLegalization { get; init; } = true;
}
