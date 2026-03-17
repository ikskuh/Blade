using Blade.Source;

namespace Blade.Semantics.Bound;

public enum BoundNodeKind
{
    Program,
    GlobalVariableMember,
    FunctionMember,

    BlockStatement,
    VariableDeclarationStatement,
    AssignmentStatement,
    ExpressionStatement,
    IfStatement,
    WhileStatement,
    ForStatement,
    LoopStatement,
    RepLoopStatement,
    RepForStatement,
    NoirqStatement,
    ReturnStatement,
    BreakStatement,
    ContinueStatement,
    YieldStatement,
    YieldtoStatement,
    AsmStatement,
    ErrorStatement,

    LiteralExpression,
    SymbolExpression,
    UnaryExpression,
    BinaryExpression,
    CallExpression,
    IntrinsicCallExpression,
    MemberAccessExpression,
    IndexExpression,
    PointerDerefExpression,
    IfExpression,
    RangeExpression,
    StructLiteralExpression,
    ConversionExpression,
    CastExpression,
    BitcastExpression,
    ErrorExpression,

    SymbolAssignmentTarget,
    MemberAssignmentTarget,
    IndexAssignmentTarget,
    PointerDerefAssignmentTarget,
    ErrorAssignmentTarget,
}

public abstract class BoundNode
{
    protected BoundNode(BoundNodeKind kind, TextSpan span)
    {
        Kind = kind;
        Span = span;
    }

    public BoundNodeKind Kind { get; }
    public TextSpan Span { get; }
}
