using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Blade;
using Blade.Semantics.Bound;
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

internal sealed class SyntheticFunctionSignatureSyntax(string name) : IFunctionSignatureSyntax
{
    public Token Name { get; } = new Token(TokenKind.Identifier, new TextSpan(0, 0), name);
    public SeparatedSyntaxList<ParameterSyntax> Parameters { get; } = new SeparatedSyntaxList<ParameterSyntax>([]);
    public Token? Arrow => null;
    public SeparatedSyntaxList<ReturnItemSyntax>? ReturnSpec => null;
}

public abstract class Symbol(string name, SourceSpan? sourceSpan = null)
{
    public string Name { get; } = Requires.NotNullOrWhiteSpace(name);
    public SourceSpan SourceSpan { get; } = sourceSpan ?? SourceSpan.Synthetic();
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

public sealed class ControlFlowLabelSymbol(string name, FunctionSymbol? function) : IAsmSymbol
{
    public ControlFlowLabelSymbol(string name)
        : this(name, function: null)
    {
    }

    private string _name = Requires.NotNullOrWhiteSpace(name);

    public string Name
    {
        get => _name;
        set => _name = Requires.NotNullOrWhiteSpace(value);
    }

    public FunctionSymbol? Function { get; } = function;
    public SymbolType SymbolType => SymbolType.ControlFlowLabel;
}

public sealed class TypeSymbol : Symbol
{
    public TypeSymbol(string name, BladeType? type = null, SourceSpan? sourceSpan = null)
        : base(name, sourceSpan)
    {
        _type = type;
    }

    private BladeType? _type;
    public bool IsResolved => _type is not null;

    public BladeType Type
    {
        get
        {
            Assert.Invariant(_type is not null, $"Type symbol '{Name}' must be resolved before its type is read.");
            return _type;
        }
    }

    public void Resolve(BladeType type)
    {
        Requires.NotNull(type);
        Assert.Invariant(_type is null, $"Type symbol '{Name}' must only be resolved once.");
        _type = type;
    }
}

public abstract class VariableSymbol(string name, BladeType type, bool isConst, SourceSpan? sourceSpan = null)
    : Symbol(name, sourceSpan)
{
    public BladeType Type { get; } = Requires.NotNull(type);
    public bool IsConst { get; } = isConst;
    public abstract VariableScopeKind ScopeKind { get; }
    public virtual bool IsAutomatic => false;
    public virtual bool IsGlobalStorage => false;
    public virtual bool IsInlineAsmTemporary => false;
    public virtual bool IsExtern => false;
    public virtual VariableStorageClass StorageClass => VariableStorageClass.Automatic;
    public virtual int? FixedAddress => null;
    public virtual int? Alignment => null;
    public virtual bool CanElideTopLevelStoreLoadChains => false;
    public bool UsesGlobalRegisterStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Reg;
    public bool UsesGlobalLutStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Lut;
    public bool UsesGlobalHubStorage => IsGlobalStorage && StorageClass == VariableStorageClass.Hub;

    public virtual void DisableTopLevelStoreLoadChainElision()
    {
    }
}

public abstract class AutomaticVariableSymbol(string name, BladeType type, bool isConst, SourceSpan? sourceSpan = null)
    : VariableSymbol(name, type, isConst, sourceSpan)
{
    public override bool IsAutomatic => true;
    public override VariableScopeKind ScopeKind => IsInlineAsmTemporary ? VariableScopeKind.InlineAsmTemporary : VariableScopeKind.Local;
}

public sealed class LocalVariableSymbol(
    string name,
    BladeType type,
    bool isConst,
    bool isInlineAsmTemporary = false,
    SourceSpan? sourceSpan = null)
    : AutomaticVariableSymbol(name, type, isConst, sourceSpan)
{
    public override bool IsInlineAsmTemporary { get; } = isInlineAsmTemporary;
}

public sealed class ParameterVariableSymbol(string name, BladeType type, SourceSpan? sourceSpan = null)
    : AutomaticVariableSymbol(name, type, isConst: false, sourceSpan)
{
    public override VariableScopeKind ScopeKind => VariableScopeKind.Parameter;
}

public sealed class GlobalVariableSymbol(
    string name,
    BladeType type,
    bool isConst,
    VariableStorageClass storageClass,
    bool isExtern,
    int? fixedAddress,
    int? alignment,
    SourceSpan? sourceSpan = null)
    : VariableSymbol(name, type, isConst, sourceSpan)
{
    private int? _fixedAddress = fixedAddress;
    private int? _alignment = alignment;
    private bool _canElideTopLevelStoreLoadChains = storageClass == VariableStorageClass.Reg
        && !isExtern
        && !fixedAddress.HasValue;

    public override bool IsGlobalStorage => true;
    public override VariableScopeKind ScopeKind => VariableScopeKind.GlobalStorage;
    public override bool IsExtern { get; } = isExtern;
    public override VariableStorageClass StorageClass { get; } = storageClass;
    public override int? FixedAddress => _fixedAddress;
    public override int? Alignment => _alignment;
    public override bool CanElideTopLevelStoreLoadChains => _canElideTopLevelStoreLoadChains;

    public BoundExpression? Initializer { get; private set; }

    public void SetLayoutMetadata(int? fixedAddress, int? alignment)
    {
        _fixedAddress = fixedAddress;
        _alignment = alignment;
        if (fixedAddress.HasValue)
            _canElideTopLevelStoreLoadChains = false;
    }

    public void SetInitializer(BoundExpression? initializer)
    {
        Initializer = initializer;
    }

    public override void DisableTopLevelStoreLoadChainElision()
    {
        _canElideTopLevelStoreLoadChains = false;
    }
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

public readonly record struct ReturnSlot(BladeType Type, ReturnPlacement Placement)
{
    public bool IsFlagPlaced => Placement is ReturnPlacement.FlagC or ReturnPlacement.FlagZ;
}

public sealed class FunctionSymbol(
    string name,
    IFunctionSignatureSyntax syntax,
    FunctionKind kind,
    FunctionInliningPolicy inliningPolicy = FunctionInliningPolicy.Default,
    SourceSpan? sourceSpan = null) : Symbol(name, sourceSpan)
{
    public FunctionSymbol(
        string name,
        FunctionKind kind,
        FunctionInliningPolicy inliningPolicy = FunctionInliningPolicy.Default)
        : this(name, new SyntheticFunctionSignatureSyntax(name), kind, inliningPolicy)
    {
    }

    public IFunctionSignatureSyntax Syntax { get; } = Requires.NotNull(syntax);
    public FunctionKind Kind { get; } = kind;
    public FunctionInliningPolicy InliningPolicy { get; } = inliningPolicy;
    public bool IsAsmFunction => Syntax is AsmFunctionDeclarationSyntax;
    public IReadOnlyList<ParameterVariableSymbol> Parameters { get; set; } = [];
    public IReadOnlyList<ReturnSlot> ReturnSlots { get; set; } = [];
    public IReadOnlyList<BladeType> ReturnTypes => System.Array.ConvertAll(
        ReturnSlots.ToArray(),
        static slot => slot.Type);
    public bool HasFlagReturns => ReturnSlots.Any(s => s.IsFlagPlaced);
}

public sealed class ModuleSymbol(string name, BoundModule module, SourceSpan? sourceSpan = null) : Symbol(name, sourceSpan)
{
    public BoundModule Module { get; } = Requires.NotNull(module);
}

public sealed class Scope(Scope? parent)
{
    private readonly Dictionary<string, Symbol> _symbols = new();

    public Scope? Parent { get; } = parent;

    public bool TryDeclare(Symbol symbol)
    {
        Requires.NotNull(symbol);

        for (Scope? scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope._symbols.ContainsKey(symbol.Name))
                return false;
        }

        _symbols.Add(symbol.Name, symbol);
        return true;
    }

    public bool ContainsInCurrentScope(string name)
    {
        Requires.NotNullOrWhiteSpace(name);
        return _symbols.ContainsKey(name);
    }

    public void DeclareInCurrentScope(Symbol symbol)
    {
        Requires.NotNull(symbol);
        Assert.Invariant(!_symbols.ContainsKey(symbol.Name), $"Symbol '{symbol.Name}' must not be redeclared in the same scope.");
        _symbols.Add(symbol.Name, symbol);
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
