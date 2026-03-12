using System;
using System.Collections.Generic;

namespace Blade.Source;

/// <summary>
/// Immutable wrapper around source text with line-index support for diagnostic locations.
/// </summary>
public sealed class SourceText
{
    private readonly string _text;
    private readonly string _filePath;
    private int[]? _lineStarts;

    public SourceText(string text, string filePath = "<input>")
    {
        _text = text;
        _filePath = filePath;
    }

    public int Length => _text.Length;

    public char this[int index] => _text[index];

    public string FilePath => _filePath;

    public string ToString(TextSpan span) => _text.Substring(span.Start, span.Length);

    public ReadOnlySpan<char> AsSpan(TextSpan span) => _text.AsSpan(span.Start, span.Length);

    public SourceLocation GetLocation(int position)
    {
        int[] lineStarts = GetLineStarts();
        int line = Array.BinarySearch(lineStarts, position);
        if (line < 0)
        {
            // BinarySearch returns ~index of next larger element
            line = ~line - 1;
        }

        int column = position - lineStarts[line] + 1;
        return new SourceLocation(_filePath, line + 1, column);
    }

    private int[] GetLineStarts()
    {
        if (_lineStarts is not null)
            return _lineStarts;

        List<int> starts = new() { 0 };
        for (int i = 0; i < _text.Length; i++)
        {
            if (_text[i] == '\n')
            {
                starts.Add(i + 1);
            }
            else if (_text[i] == '\r')
            {
                if (i + 1 < _text.Length && _text[i + 1] == '\n')
                    i++; // skip \n in \r\n
                starts.Add(i + 1);
            }
        }

        _lineStarts = starts.ToArray();
        return _lineStarts;
    }
}
