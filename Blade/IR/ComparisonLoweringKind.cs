namespace Blade.IR;

public enum ComparisonLoweringKind
{
    Default,
    SignedOrder,
    NegativeBitTest,
    NonNegativeBitTest,
}