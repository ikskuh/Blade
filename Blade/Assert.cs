
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Blade;

[ExcludeFromCodeCoverage]
internal static class Assert
{
    [DoesNotReturn]
    [ExcludeFromCodeCoverage]
    public static T UnreachableTypePattern<T>(Type t) => throw new UnreachableException($"The type {t} should not be reachable at this point.");
}