using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Blade.Syntax.Nodes;

/// <summary>
/// A comma-separated list of syntax nodes, preserving separator tokens.
/// Internal storage is interleaved: [node, separator, node, separator, node].
/// </summary>
public sealed class SeparatedSyntaxList<T>(IReadOnlyList<object> nodesAndSeparators) : IEnumerable<T> where T : SyntaxNode
{
    private readonly IReadOnlyList<object> _nodesAndSeparators = nodesAndSeparators;

    public int Count => (_nodesAndSeparators.Count + 1) / 2;

    public T this[int index] => (T)_nodesAndSeparators[index * 2];

    [ExcludeFromCodeCoverage]
    public Token GetSeparator(int index) => (Token)_nodesAndSeparators[(index * 2) + 1];

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
