using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Blade;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Semantics;

public enum SymbolType
{
    Function,
    Parameter,
    AutomaticVariable,
    RegVariable,
    LutVariable,
    HubVariable,
    ControlFlowLabel,
}

public interface IAsmSymbol
{
    string Name { get; }
    SymbolType SymbolType { get; }
}

internal sealed class SyntheticFunctionSignatureSyntax : IFunctionSignatureSyntax
{
    public SyntheticFunctionSignatureSyntax(string name)
    {
        Name = new Token(TokenKind.Identifier, new TextSpan(0, 0), name);
        Parameters = new SeparatedSyntaxList<ParameterSyntax>([]);
    }

    public Token Name { get; }
    public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }
    public Token? Arrow => null;
    public SeparatedSyntaxList<ReturnItemSyntax>? ReturnSpec => null;
}

public abstract class Symbol
{
    protected Symbol(string name)
    {
        Name = Requires.NotNullOrWhiteSpace(name);
    }

    public string Name { get; }
}

public sealed class AbsoluteAddressSymbol(int address, VariableStorageClass storageClass) : Symbol(BuildName(address, storageClass))
{
    public int Address { get; } = address;
    public VariableStorageClass StorageClass { get; } = storageClass;

    private static string BuildName(int address, VariableStorageClass storageClass)
    {
        return storageClass switch
        {
            VariableStorageClass.Lut => $"abs_lut_{address}",
            VariableStorageClass.Hub => $"abs_hub_{address}",
            _ => $"abs_reg_{address}",
        };
    }
}

public sealed class ControlFlowLabelSymbol : IAsmSymbol
{
    public ControlFlowLabelSymbol(string name)
        : this(name, function: null)
    {
    }

    public ControlFlowLabelSymbol(string name, FunctionSymbol? function)
    {
        _name = Requires.NotNullOrWhiteSpace(name);
        Function = function;
    }

    private string _name;

    public string Name
    {
        get => _name;
        set => _name = Requires.NotNullOrWhiteSpace(value);
    }

    public FunctionSymbol? Function { get; }
    public SymbolType SymbolType => SymbolType.ControlFlowLabel;
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
        int? alignment,
        RuntimeBladeValue? constantValue = null)
        : base(name)
    {
        Type = Requires.NotNull(type);
        IsConst = isConst;
        StorageClass = storageClass;
        ScopeKind = scopeKind;
        IsExtern = isExtern;
        FixedAddress = fixedAddress;
        Alignment = alignment;
        ConstantValue = constantValue;
        CanElideTopLevelStoreLoadChains = scopeKind == VariableScopeKind.GlobalStorage
            && storageClass == VariableStorageClass.Reg
            && !isExtern
            && !fixedAddress.HasValue;
    }

    public TypeSymbol Type { get; }
    public bool IsConst { get; }
    public VariableStorageClass StorageClass { get; }
    public VariableScopeKind ScopeKind { get; }
    public bool IsExtern { get; }
    public int? FixedAddress { get; private set; }
    public int? Alignment { get; private set; }

    public bool IsAutomatic => ScopeKind is VariableScopeKind.Local or VariableScopeKind.TopLevelAutomatic or VariableScopeKind.InlineAsmTemporary;
    public bool IsGlobalStorage => ScopeKind == VariableScopeKind.GlobalStorage;
    public bool UsesGlobalRegisterStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Reg;
    public bool UsesGlobalLutStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Lut;
    public bool UsesGlobalHubStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Hub;
    public RuntimeBladeValue? ConstantValue { get; }
    public bool CanElideTopLevelStoreLoadChains { get; private set; }

    public void SetLayoutMetadata(int? fixedAddress, int? alignment)
    {
        FixedAddress = fixedAddress;
        Alignment = alignment;
        if (fixedAddress.HasValue)
            CanElideTopLevelStoreLoadChains = false;
    }

    public void DisableTopLevelStoreLoadChainElision()
    {
        CanElideTopLevelStoreLoadChains = false;
    }

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
    InlineAsmTemporary,
    GlobalStorage,
}

public enum FunctionKind
{
    Default,
    Leaf,
    Rec,
    Coro,
    Comptime,
    Int1,
    Int2,
    Int3,
}

public enum FunctionInliningPolicy
{
    Default,
    ForceInline,
    NeverInline,
}

public enum ReturnPlacement
{
    Register,
    FlagC,
    FlagZ,
}

public readonly record struct ReturnSlot(TypeSymbol Type, ReturnPlacement Placement)
{
    public bool IsFlagPlaced => Placement is ReturnPlacement.FlagC or ReturnPlacement.FlagZ;
}

public sealed class FunctionSymbol : Symbol
{
    public FunctionSymbol(
        string name,
        IFunctionSignatureSyntax syntax,
        FunctionKind kind,
        FunctionInliningPolicy inliningPolicy = FunctionInliningPolicy.Default)
        : base(name)
    {
        Syntax = Requires.NotNull(syntax);
        Kind = kind;
        InliningPolicy = inliningPolicy;
    }

    public FunctionSymbol(
        string name,
        FunctionKind kind,
        FunctionInliningPolicy inliningPolicy = FunctionInliningPolicy.Default)
        : this(name, new SyntheticFunctionSignatureSyntax(name), kind, inliningPolicy)
    {
    }

    public IFunctionSignatureSyntax Syntax { get; }
    public FunctionKind Kind { get; }
    public FunctionInliningPolicy InliningPolicy { get; }
    public bool IsAsmFunction => Syntax is AsmFunctionDeclarationSyntax;
    public IReadOnlyList<ParameterSymbol> Parameters { get; set; } = [];
    public IReadOnlyList<ReturnSlot> ReturnSlots { get; set; } = [];
    public IReadOnlyList<TypeSymbol> ReturnTypes => System.Array.ConvertAll(
        ReturnSlots.ToArray(),
        static slot => slot.Type);
    public bool HasFlagReturns => ReturnSlots.Any(s => s.IsFlagPlaced);
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
