using System;

namespace Blade;

/// <summary>
/// Provides compiler-side parsing and text helpers for the generated P2 metadata enums.
/// </summary>
internal static class P2MetadataSyntax
{
    public static bool TryParseMnemonic(string text, out P2Mnemonic mnemonic)
        => Enum.TryParse(Requires.NotNullOrWhiteSpace(text), ignoreCase: true, out mnemonic);

    public static bool TryParseConditionCode(string text, out P2ConditionCode conditionCode)
    {
        string normalized = Requires.NotNullOrWhiteSpace(text);
        if (string.Equals(normalized, "<INST>", StringComparison.OrdinalIgnoreCase))
        {
            conditionCode = P2ConditionCode.INST;
            return true;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out conditionCode);
    }

    public static string GetConditionPrefixText(P2ConditionCode conditionCode)
        => conditionCode.GetText();

    public static bool TryParseFlagEffect(string text, out P2FlagEffect flagEffect)
    {
        bool parsed = P2FlagEffectExtensions.TryParse(Requires.NotNullOrWhiteSpace(text), out flagEffect);
        return parsed && flagEffect != P2FlagEffect.None;
    }

    public static bool TryParseSpecialRegister(string text, out P2SpecialRegister register)
        => Enum.TryParse(Requires.NotNullOrWhiteSpace(text), ignoreCase: true, out register);
}
