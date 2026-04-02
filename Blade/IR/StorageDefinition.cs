using Blade.Semantics;

namespace Blade.IR;

public sealed class StorageDefinition(StoragePlace place, RuntimeBladeValue? initialValue = null)
{
    public StoragePlace Place { get; } = Requires.NotNull(place);
    public RuntimeBladeValue? InitialValue { get; } = initialValue;
}
