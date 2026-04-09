using System.Collections.Generic;
using Blade.Semantics.Bound;

namespace Blade.Semantics;

internal sealed class ImportedModuleDefinition(BoundModule module, IReadOnlyDictionary<Symbol, ComptimeResult> knownConstantValues)
{
    public BoundModule Module { get; } = Requires.NotNull(module);
    public IReadOnlyDictionary<Symbol, ComptimeResult> KnownConstantValues { get; } = Requires.NotNull(knownConstantValues);
}
