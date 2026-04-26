using System;
using System.IO;
using System.Text;
using Blade.Diagnostics;

namespace Blade.Source;

public static class SourceFileLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryLoad(string filePath, DiagnosticBag diagnostics, out SourceText source)
    {
        Requires.NotNullOrWhiteSpace(filePath);
        Requires.NotNull(diagnostics);

        byte[] bytes = File.ReadAllBytes(filePath);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            source = new SourceText(string.Empty, filePath);
            using IDisposable _ = diagnostics.UseSource(source);
            diagnostics.Report(new InvalidUtf8Error(diagnostics.CurrentSource, new TextSpan(0, 0)));
            return false;
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        source = new SourceText(text, filePath);
        return Validate(source, diagnostics);
    }

    public static bool Validate(SourceText source, DiagnosticBag diagnostics)
    {
        Requires.NotNull(source);
        Requires.NotNull(diagnostics);

        bool isValid = true;
        using IDisposable _ = diagnostics.UseSource(source);
        for (int i = 0; i < source.Length; i++)
        {
            char current = source[i];
            if (current is '\n' or '\r' or '\t' or ' ')
                continue;

            if (char.IsControl(current))
            {
                diagnostics.Report(new InvalidControlCharacterError(diagnostics.CurrentSource, new TextSpan(i, 1), current));
                isValid = false;
            }
        }

        return isValid;
    }
}
