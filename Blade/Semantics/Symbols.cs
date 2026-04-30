using System;
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

public abstract class Symbol(string name, SourceSpan? sourceSpan = null)
{
    public string Name { get; } = Requires.NotNullOrWhiteSpace(name);
    public SourceSpan SourceSpan { get; } = sourceSpan ?? SourceSpan.Synthetic();
}

public sealed class AbsoluteAddressSymbol(VirtualAddress address) : Symbol(BuildName(address))
{
    public VirtualAddress Address { get; } = address;
    public AddressSpace StorageClass => Address.AddressSpace;

    private static string BuildName(VirtualAddress address)
    {
        return address.AddressSpace switch
        {
            AddressSpace.Lut => $"abs_lut_{(int)address.ToLutAddress()}",
            AddressSpace.Hub => $"abs_hub_{(int)address.ToHubAddress()}",
            _ => $"abs_reg_{(int)address.ToCogAddress()}",
        };
    }
}

public sealed class ControlFlowLabelSymbol(string name, FunctionSymbol? function) : IAsmSymbol
{
    public ControlFlowLabelSymbol(string name)
        : this(name, function: null)
    {
    }

    public string Name { get; } = Requires.NotNullOrWhiteSpace(name);

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
}

public abstract class AutomaticVariableSymbol(string name, BladeType type, bool isConst, SourceSpan? sourceSpan = null)
    : VariableSymbol(name, type, isConst, sourceSpan)
{

}

public sealed class LocalVariableSymbol(
    string name,
    BladeType type,
    bool isConst,
    bool isInlineAsmTemporary = false,
    SourceSpan? sourceSpan = null)
    : AutomaticVariableSymbol(name, type, isConst, sourceSpan)
{
    public bool IsInlineAsmTemporary { get; } = isInlineAsmTemporary;
    public override VariableScopeKind ScopeKind => IsInlineAsmTemporary ? VariableScopeKind.InlineAsmTemporary : VariableScopeKind.Local;
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
    AddressSpace storageClass,
    LayoutSymbol? declaringLayout,
    bool isExtern,
    VirtualAddress? fixedAddress,
    int? alignment,
    SourceSpan? sourceSpan = null)
    : VariableSymbol(name, type, isConst, sourceSpan)
{
    private VirtualAddress? _fixedAddress = fixedAddress;
    private int? _alignment = alignment;
    private LayoutSymbol? _declaringLayout = declaringLayout;
    private bool _canElideTopLevelStoreLoadChains = storageClass == AddressSpace.Cog
        && !isExtern
        && !fixedAddress.HasValue;

    public override VariableScopeKind ScopeKind => VariableScopeKind.GlobalStorage;
    public bool IsExtern { get; } = isExtern;
    public AddressSpace StorageClass { get; } = storageClass;
    public VirtualAddress? FixedAddress => _fixedAddress;
    public int? Alignment => _alignment;
    public bool CanElideTopLevelStoreLoadChains => _canElideTopLevelStoreLoadChains;

    /// <summary>
    /// Gets the layout that directly declared this variable, or <see langword="null"/>
    /// for plain top-level globals.
    /// </summary>
    public LayoutSymbol? DeclaringLayout => _declaringLayout;

    public BoundExpression? Initializer { get; private set; }

    public void SetLayoutMetadata(VirtualAddress? fixedAddress, int? alignment)
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

    public void DisableTopLevelStoreLoadChainElision()
    {
        _canElideTopLevelStoreLoadChains = false;
    }

    internal void RetargetDeclaringLayout(LayoutSymbol? declaringLayout)
    {
        _declaringLayout = declaringLayout;
    }
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
    bool isTopLevel,
    AddressSpace? storageClass,
    FunctionInliningPolicy inliningPolicy,
    SourceSpan? sourceSpan) : Symbol(name, sourceSpan)
{
    private int? _alignment;
    private IReadOnlyList<LayoutSymbol> _associatedLayouts = [];
    private LayoutSymbol? _implicitLayout;

    internal IFunctionSignatureSyntax SignatureSyntax { get; } = Requires.NotNull(syntax);
    internal TextSpan SignatureNameSpan { get; } = Requires.NotNull(syntax).Name.Span;
    internal SeparatedSyntaxList<ParameterSyntax> SignatureParameters { get; } = syntax.Parameters;
    internal SeparatedSyntaxList<ReturnItemSyntax>? SignatureReturnSpec { get; } = syntax.ReturnSpec;
    public FunctionKind Kind { get; } = kind;
    public bool IsTopLevel { get; } = isTopLevel;
    public AddressSpace? StorageClass { get; private set; } = storageClass;
    public FunctionInliningPolicy InliningPolicy { get; } = inliningPolicy;
    public int? Alignment => _alignment;
    public IReadOnlyList<LayoutSymbol> AssociatedLayouts => _associatedLayouts;
    public LayoutSymbol? ImplicitLayout => _implicitLayout;
    public IReadOnlyList<ParameterVariableSymbol> Parameters { get; set; } = [];
    public IReadOnlyList<ReturnSlot> ReturnSlots { get; set; } = [];
    public IReadOnlyList<BladeType> ReturnTypes => System.Array.ConvertAll(
        ReturnSlots.ToArray(),
        static slot => slot.Type);

    public void SetMetadata(int? alignment, IReadOnlyList<LayoutSymbol> associatedLayouts)
    {
        _alignment = alignment;
        _associatedLayouts = Requires.NotNull(associatedLayouts);
    }

    public void SetImplicitLayout(LayoutSymbol? implicitLayout)
    {
        _implicitLayout = implicitLayout;
    }
}

public sealed class ModuleSymbol(string name, BoundModule module, SourceSpan? sourceSpan = null) : Symbol(name, sourceSpan)
{
    public BoundModule Module { get; } = Requires.NotNull(module);
}

/// <summary>
/// Represents a named storage layout whose members may be imported into task scope.
/// </summary>
public class LayoutSymbol(string name, SourceSpan? sourceSpan = null) : Symbol(name, sourceSpan)
{
    private IReadOnlyList<LayoutSymbol> _parents = [];
    private readonly Dictionary<string, GlobalVariableSymbol> _declaredMembers = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the layouts that this layout inherits from.
    /// </summary>
    public IReadOnlyList<LayoutSymbol> Parents => _parents;

    /// <summary>
    /// Gets the members declared directly by this layout.
    /// </summary>
    public IReadOnlyDictionary<string, GlobalVariableSymbol> DeclaredMembers => _declaredMembers;

    /// <summary>
    /// Gets a value indicating whether this layout is the implicit private layout attached to a task.
    /// </summary>
    public virtual bool IsTaskLayout => false;

    /// <summary>
    /// Replaces the direct parent-layout list associated with this layout.
    /// </summary>
    public void SetParents(IReadOnlyList<LayoutSymbol> parents)
    {
        _parents = Requires.NotNull(parents);
    }

    /// <summary>
    /// Tries to add a directly declared member to this layout.
    /// </summary>
    public bool TryDeclareMember(GlobalVariableSymbol variable)
    {
        Requires.NotNull(variable);
        return _declaredMembers.TryAdd(variable.Name, variable);
    }
}

/// <summary>
/// Represents a task declaration together with its implicit private layout.
/// </summary>
public sealed class TaskSymbol(string name, FunctionSymbol entryFunction, AddressSpace storageClass, SourceSpan? sourceSpan = null)
    : LayoutSymbol(name, sourceSpan)
{
    private FunctionSymbol _entryFunction = Requires.NotNull(entryFunction);

    /// <summary>
    /// Gets the entry function that executes this task body.
    /// </summary>
    public FunctionSymbol EntryFunction => _entryFunction;

    /// <summary>
    /// Gets the execution/storage space in which this task starts.
    /// </summary>
    public AddressSpace StorageClass { get; } = storageClass;

    /// <summary>
    /// Gets a value indicating that this layout is the implicit private layout attached to a task.
    /// </summary>
    public override bool IsTaskLayout => true;

    internal void RetargetEntryFunction(FunctionSymbol entryFunction)
    {
        _entryFunction = Requires.NotNull(entryFunction);
    }
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
