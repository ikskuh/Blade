using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Blade;

public static class Requires
{
    public static void That(
        bool invariant,
        [CallerArgumentExpression(nameof(invariant))] string? invariantExpr = null)
    {
        if (!invariant)
            throw new ArgumentException($"Condition {invariantExpr} did not hold!");
    }

    public static T NotNull<T>(
        [NotNull] T? value,
        [CallerArgumentExpression("value")] string? parameterName = null)
        where T : class
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        return value;
    }

    public static string NotNullOrWhiteSpace(
        [NotNull] string? value,
        [CallerArgumentExpression("value")] string? parameterName = null)
    {
        NotNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName);
        return value;
    }

    public static int NonNegative(
        int value,
        [CallerArgumentExpression("value")] string? parameterName = null)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be non-negative.");
        return value;
    }

    public static int Positive(
        int value,
        [CallerArgumentExpression("value")] string? parameterName = null)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be positive.");
        return value;
    }

    public static int InRange(
        int value,
        int minInclusive,
        int maxInclusive,
        [CallerArgumentExpression("value")] string? parameterName = null)
    {
        if (value < minInclusive || value > maxInclusive)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be in the range [{minInclusive}, {maxInclusive}].");
        }

        return value;
    }
}
