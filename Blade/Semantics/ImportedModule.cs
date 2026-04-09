using System.Collections.Generic;
using Blade.Semantics.Bound;

namespace Blade.Semantics;

internal sealed class ImportedModuleDefinition
{
    public ImportedModuleDefinition(BoundModule module, IReadOnlyDictionary<Symbol, ComptimeResult> knownConstantValues)
    {
        Module = Requires.NotNull(module);
        KnownConstantValues = Requires.NotNull(knownConstantValues);
    }

    public BoundModule Module { get; }
    public IReadOnlyDictionary<Symbol, ComptimeResult> KnownConstantValues { get; }
}
