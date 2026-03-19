using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Blade;
using Blade.Syntax.Nodes;

namespace Blade.Semantics;

public abstract class Symbol
{
    private static int _nextId;

    protected Symbol(string name)
    {
        Id = Interlocked.Increment(ref _nextId);
        Name = name;
    }

    public int Id { get; }
    public string Name { get; }
}

public sealed class TypeAliasSymbol : Symbol
{
    public TypeAliasSymbol(string name, TypeAliasDeclarationSyntax syntax)
        : base(name)
    {
        Syntax = Requires.NotNull(syntax);
    }

    public TypeAliasDeclarationSyntax Syntax { get; }
}

public sealed class VariableSymbol : Symbol
{
    public VariableSymbol(
        string name,
        TypeSymbol type,
        bool isConst,
        VariableStorageClass storageClass,
        VariableScopeKind scopeKind,
        bool isExtern,
        int? fixedAddress,
        int? alignment)
        : base(name)
    {
        Type = Requires.NotNull(type);
        IsConst = isConst;
        StorageClass = storageClass;
        ScopeKind = scopeKind;
        IsExtern = isExtern;
        FixedAddress = fixedAddress;
        Alignment = alignment;
    }

    public TypeSymbol Type { get; }
    public bool IsConst { get; }
    public VariableStorageClass StorageClass { get; }
    public VariableScopeKind ScopeKind { get; }
    public bool IsExtern { get; }
    public int? FixedAddress { get; }
    public int? Alignment { get; }

    public bool IsAutomatic => ScopeKind is VariableScopeKind.Local or VariableScopeKind.TopLevelAutomatic;
    public bool IsGlobalStorage => ScopeKind == VariableScopeKind.GlobalStorage;
    public bool UsesGlobalRegisterStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Reg;
    public bool UsesGlobalLutStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Lut;
    public bool UsesGlobalHubStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Hub;
}

public sealed class ParameterSymbol : Symbol
{
    public ParameterSymbol(string name, TypeSymbol type)
        : base(name)
    {
        Type = Requires.NotNull(type);
    }

    public TypeSymbol Type { get; }
}

public enum VariableStorageClass
{
    Automatic,
    Reg,
    Lut,
    Hub,
}

public enum VariableScopeKind
{
    Local,
    Parameter,
    TopLevelAutomatic,
    GlobalStorage,
}

public enum FunctionKind
{
    Default,
    Leaf,
    Inline,
    Noinline,
    Rec,
    Coro,
    Comptime,
    Int1,
    Int2,
    Int3,
}

public sealed class FunctionSymbol : Symbol
{
    public FunctionSymbol(string name, IFunctionSignatureSyntax syntax, FunctionKind kind)
        : base(name)
    {
        Syntax = Requires.NotNull(syntax);
        Kind = kind;
    }

    public IFunctionSignatureSyntax Syntax { get; }
    public FunctionKind Kind { get; }
    public bool IsAsmFunction => Syntax is AsmFunctionDeclarationSyntax;
    public IReadOnlyList<ParameterSymbol> Parameters { get; set; } = [];
    public IReadOnlyList<TypeSymbol> ReturnTypes { get; set; } = [];
}

public sealed class ModuleSymbol : Symbol
{
    public ModuleSymbol(string name, ImportedModule module)
        : base(name)
    {
        Module = Requires.NotNull(module);
    }

    public ImportedModule Module { get; }
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
        Requires.NotNull(symbol);

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

    [SuppressMessage(
        "Design",
        "CA1024:Use properties where appropriate",
        Justification = "Enumerating declared names is an action-oriented query, not stable object state.")]
    public IEnumerable<string> GetDeclaredNames() => _symbols.Keys;
}
