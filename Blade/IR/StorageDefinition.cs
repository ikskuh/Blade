using System.Collections.Generic;
using Blade.Semantics;

namespace Blade.IR;

public sealed class StorageDefinition(StoragePlace place, IReadOnlyList<RuntimeBladeValue>? initialValues = null)
{
    public StoragePlace Place { get; } = Requires.NotNull(place);
    public IReadOnlyList<RuntimeBladeValue>? InitialValues { get; } = initialValues;
}
