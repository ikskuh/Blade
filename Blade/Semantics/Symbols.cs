using System.Collections.Generic;
using Blade.Syntax.Nodes;

namespace Blade.Semantics;

public abstract class Symbol
{
    protected Symbol(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

public sealed class TypeAliasSymbol : Symbol
{
    public TypeAliasSymbol(string name, TypeAliasDeclarationSyntax syntax)
        : base(name)
    {
        Syntax = syntax;
    }

    public TypeAliasDeclarationSyntax Syntax { get; }
}

public sealed class VariableSymbol : Symbol
{
    public VariableSymbol(string name, TypeSymbol type, bool isConst)
        : base(name)
    {
        Type = type;
        IsConst = isConst;
    }

    public TypeSymbol Type { get; }
    public bool IsConst { get; }
}

public sealed class ParameterSymbol : Symbol
{
    public ParameterSymbol(string name, TypeSymbol type)
        : base(name)
    {
        Type = type;
    }

    public TypeSymbol Type { get; }
}

public enum FunctionKind
{
    Default,
    Leaf,
    Inline,
    Rec,
    Coro,
    Comptime,
    Int1,
    Int2,
    Int3,
}

public sealed class FunctionSymbol : Symbol
{
    public FunctionSymbol(string name, FunctionDeclarationSyntax syntax, FunctionKind kind)
        : base(name)
    {
        Syntax = syntax;
        Kind = kind;
    }

    public FunctionDeclarationSyntax Syntax { get; }
    public FunctionKind Kind { get; }
    public IReadOnlyList<ParameterSymbol> Parameters { get; set; } = [];
    public IReadOnlyList<TypeSymbol> ReturnTypes { get; set; } = [];
}

public sealed class Scope
{
    private readonly Dictionary<string, Symbol> _symbols = new();

    public Scope(Scope? parent)
    {
        Parent = parent;
    }

    public Scope? Parent { get; }

    public bool TryDeclare(Symbol symbol)
    {
        if (_symbols.ContainsKey(symbol.Name))
            return false;

        _symbols.Add(symbol.Name, symbol);
        return true;
    }

    public bool TryLookup(string name, out Symbol? symbol)
    {
        if (_symbols.TryGetValue(name, out Symbol? local))
        {
            symbol = local;
            return true;
        }

        if (Parent is not null)
            return Parent.TryLookup(name, out symbol);

        symbol = null;
        return false;
    }
}
