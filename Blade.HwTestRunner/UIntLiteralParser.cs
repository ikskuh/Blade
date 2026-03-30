using System;

namespace Blade.HwTestRunner;

public static class UIntLiteralParser
{
    public static uint Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToUInt32(text[2..], 16);

        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return Convert.ToUInt32(text[2..], 2);

        if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            return Convert.ToUInt32(text[2..], 8);

        return Convert.ToUInt32(text, 10);
    }
}
