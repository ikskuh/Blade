
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Blade;

/// <summary>
/// A collection of safety guards for defensive programming.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class Assert
{
    internal enum ParameterGuard
    {
        /// <summary>
        /// Guard value for compiler service arguments
        /// </summary>
        DoNotWriteThis,
    };

    /// <summary>
    /// Checks an invariant.
    /// </summary>
    /// <param name="condition">The invariant to be checked. Crashes when false.</param>
    /// <param name="message"></param>
    /// <param name="expression"></param>
    /// <param name="file"></param>
    /// <param name="line"></param>
    /// <param name="member"></param>
    /// <exception cref="UnreachableException"></exception>
    public static void Invariant([DoesNotReturnIf(false)] bool condition, string message = "", ParameterGuard _guard = ParameterGuard.DoNotWriteThis, [CallerArgumentExpression(nameof(condition))] string expression = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        if (!condition)
        {
            string detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $": {message}";
            throw new UnreachableException($"Invariant {expression} does not hold true in {member} ({file}:{line}){detail}");
        }
    }
    
    /// <summary>
    /// Checks if a value is not null.
    /// </summary>
    /// <param name="value">The invariant to be checked. Crashes when false.</param>
    /// <param name="message"></param>
    /// <param name="expression"></param>
    /// <param name="file"></param>
    /// <param name="line"></param>
    /// <param name="member"></param>
    /// <exception cref="UnreachableException"></exception>
    public static T NotNull<T>(T? value, string message = "", ParameterGuard _guard = ParameterGuard.DoNotWriteThis, [CallerArgumentExpression(nameof(value))] string expression = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        if (value is null)
        {
            string detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $": {message}";
            throw new UnreachableException($"{expression} was null {member} ({file}:{line}){detail}");
        }
        return value;
    }

    /// <summary>
    /// Unreachable statement.
    /// </summary>
    /// <param name="guard"></param>
    /// <param name="file"></param>
    /// <param name="line"></param>
    /// <param name="member"></param>
    [DoesNotReturn]
    public static void Unreachable(ParameterGuard guard = ParameterGuard.DoNotWriteThis, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        _ = guard;
        throw CreateUnreachableException("", file, line, member);
    }

    [DoesNotReturn]
    public static void Unreachable(string message, ParameterGuard guard = ParameterGuard.DoNotWriteThis, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        _ = guard;
        throw CreateUnreachableException(message, file, line, member);
    }

    /// <summary>
    /// Unreachable expression.
    /// </summary>
    /// <typeparam name="T">The value this function *would* return if it *could* return.</typeparam>
    /// <param name="guard"></param>
    /// <param name="file"></param>
    /// <param name="line"></param>
    /// <param name="member"></param>
    /// <returns></returns>
    [DoesNotReturn]
    public static T UnreachableValue<T>(ParameterGuard guard = ParameterGuard.DoNotWriteThis, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        _ = guard;
        throw CreateUnreachableException("", file, line, member);
    }

    /// <summary>
    /// Unreachable expression.
    /// </summary>
    /// <typeparam name="T">The value this function *would* return if it *could* return.</typeparam>
    /// <param name="message"></param>
    /// <param name="guard"></param>
    /// <param name="file"></param>
    /// <param name="line"></param>
    /// <param name="member"></param>
    /// <returns></returns>
    [DoesNotReturn]
    public static T UnreachableValue<T>(string message, ParameterGuard guard = ParameterGuard.DoNotWriteThis, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0, [CallerMemberName] string member = "")
    {
        _ = guard;
        throw CreateUnreachableException(message, file, line, member);
    }

    /// <summary>
    /// Constructs a new unreachable exception from the given parameters.
    /// </summary>
    /// <param name="file"></param>
    /// <param name="line"></param>
    /// <param name="member"></param>
    /// <returns></returns>
    private static UnreachableException CreateUnreachableException(string message, string? file, int? line, string? member)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new UnreachableException($"reached unreachable code in {member} ({file}:{line})");
        }
        else
        {
            return new UnreachableException($"reached unreachable code in {member} ({file}:{line}): {message}");
        }
    }
}
