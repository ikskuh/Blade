using Blade.Source;

namespace Blade.Semantics.Bound;

public enum BoundNodeKind
{
    Program,
    Module,
    GlobalVariableMember,
    FunctionMember,

    BlockStatement,
    VariableDeclarationStatement,
    AssignmentStatement,
    MultiAssignmentStatement,
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
    SpawnExpression,
    IntrinsicCallExpression,
    EnumLiteralExpression,
    ArrayLiteralExpression,
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
    BitfieldAssignmentTarget,
    IndexAssignmentTarget,
    PointerDerefAssignmentTarget,
    DiscardAssignmentTarget,
    ErrorAssignmentTarget,
}

public abstract class BoundNode(BoundNodeKind kind, TextSpan span)
{
    public BoundNodeKind Kind { get; } = kind;
    public TextSpan Span { get; } = span;
}
