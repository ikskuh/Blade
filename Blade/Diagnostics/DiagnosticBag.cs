using System;
using System.Collections;
using System.Collections.Generic;
using Blade;
using Blade.Source;

namespace Blade.Diagnostics;

/// <summary>
/// Collects diagnostics during compilation.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = new();
    private SourceText? _currentSource;

    /// <summary>
    /// Gets the number of collected diagnostics.
    /// </summary>
    public int Count => _diagnostics.Count;

    /// <summary>
    /// Gets the number of collected error diagnostics.
    /// </summary>
    public int ErrorCount
    {
        get
        {
            int count = 0;
            foreach (Diagnostic diagnostic in _diagnostics)
            {
                if (diagnostic.IsError)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Gets whether any collected diagnostic is an error.
    /// </summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>
    /// Uses a source text as the current located diagnostic context until the returned scope is disposed.
    /// </summary>
    public IDisposable UseSource(SourceText source)
    {
        Requires.NotNull(source);
        SourceText? previous = _currentSource;
        _currentSource = source;
        return new SourceScope(this, previous);
    }

    /// <summary>
    /// Adds a typed diagnostic message to the bag.
    /// </summary>
    public void Report(DiagnosticMessage message)
    {
        _diagnostics.Add(new Diagnostic(Requires.NotNull(message)));
    }

    /// <summary>
    /// Gets the source currently used for located diagnostics.
    /// </summary>
    public SourceText CurrentSource
    {
        get
        {
            Assert.Invariant(_currentSource is not null, "Diagnostics require a current source context. Use DiagnosticBag.UseSource(source) when reporting.");
            return _currentSource;
        }
    }

    /// <summary>
    /// Reports a failed static assertion, adding the optional assertion message when present.
    /// </summary>
    public void ReportAssertionFailed(TextSpan span, string? message)
    {
        string assertionMessage = message is null ? "assertion failed" : $"assertion failed: {message}";
        Report(new AssertionFailedError(CurrentSource, span, assertionMessage));
    }

    /// <summary>
    /// Reports a layout member lookup that matched more than one layout.
    /// </summary>
    public void ReportAmbiguousLayoutMemberAccess(TextSpan span, string name, IReadOnlyList<string> layoutNames)
    {
        string layoutNamesText = string.Join(", ", layoutNames);
        Report(new AmbiguousLayoutMemberAccessError(CurrentSource, span, name, layoutNamesText));
    }

    /// <summary>
    /// Reports a lexical name that hides one or more available layout members.
    /// </summary>
    public void ReportLexicalNameConflictsWithLayoutMember(TextSpan span, string name, IReadOnlyList<string> layoutNames)
    {
        string layoutNamesText = string.Join(", ", layoutNames);
        Report(new LexicalNameConflictsWithLayoutMemberWarning(CurrentSource, span, name, layoutNamesText));
    }

    /// <summary>
    /// Reports a non-cog entry task and formats its storage class for the diagnostic message.
    /// </summary>
    public void ReportMainTaskMustBeCog(TextSpan span, string taskName, Blade.Semantics.VariableStorageClass storageClass)
    {
        string storageClassKeyword = storageClass switch
        {
            Blade.Semantics.VariableStorageClass.Cog => "cog",
            Blade.Semantics.VariableStorageClass.Lut => "lut",
            Blade.Semantics.VariableStorageClass.Hub => "hub",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };

        Report(new MainTaskMustBeCogWarning(CurrentSource, span, taskName, storageClassKeyword));
    }

    /// <summary>
    /// Reports an invalid fixed layout address after formatting the storage class.
    /// </summary>
    public void ReportInvalidLayoutAddress(TextSpan span, string layoutName, string memberName, Blade.Semantics.VariableStorageClass storageClass, int address, int sizeInAddressUnits)
    {
        string storageName = FormatStorageClass(storageClass);
        Report(new InvalidLayoutAddressError(CurrentSource, span, layoutName, memberName, storageName, address, sizeInAddressUnits));
    }

    /// <summary>
    /// Reports two fixed layout addresses that conflict after formatting the storage class.
    /// </summary>
    public void ReportLayoutAddressConflict(TextSpan span, string layoutName, string memberName, Blade.Semantics.VariableStorageClass storageClass, int address, string conflictingLayoutName, string conflictingMemberName, int conflictingAddress)
    {
        string storageName = FormatStorageClass(storageClass);
        Report(new LayoutAddressConflictError(CurrentSource, span, layoutName, memberName, storageName, address, conflictingLayoutName, conflictingMemberName, conflictingAddress));
    }

    /// <summary>
    /// Reports a failed automatic layout allocation after formatting the storage class.
    /// </summary>
    public void ReportLayoutAllocationFailed(TextSpan span, string layoutName, string memberName, Blade.Semantics.VariableStorageClass storageClass, int sizeInAddressUnits, int alignmentInAddressUnits)
    {
        string storageName = FormatStorageClass(storageClass);
        Report(new LayoutAllocationFailedError(CurrentSource, span, layoutName, memberName, storageName, sizeInAddressUnits, alignmentInAddressUnits));
    }

    /// <summary>
    /// Enumerates all collected diagnostics.
    /// </summary>
    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static string FormatStorageClass(Blade.Semantics.VariableStorageClass storageClass)
    {
        return storageClass switch
        {
            Blade.Semantics.VariableStorageClass.Cog => "cog",
            Blade.Semantics.VariableStorageClass.Lut => "lut",
            Blade.Semantics.VariableStorageClass.Hub => "hub",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }

    private readonly struct SourceScope(DiagnosticBag bag, SourceText? previous) : IDisposable
    {
        private readonly DiagnosticBag _bag = bag;
        private readonly SourceText? _previous = previous;

        public void Dispose()
        {
            _bag._currentSource = _previous;
        }
    }
}
