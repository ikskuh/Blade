using System.Text.Json.Serialization;

namespace Blade.P2MetadataGen;

internal sealed class MetadataJsonRoot
{
    [JsonPropertyName("conditionCodes")]
    public required Dictionary<string, AliasEntryJson> ConditionCodes { get; init; }

    [JsonPropertyName("modczOperands")]
    public required Dictionary<string, AliasEntryJson> ModczOperands { get; init; }

    [JsonPropertyName("specialRegisters")]
    public required Dictionary<string, SpecialRegisterJson> SpecialRegisters { get; init; }

    [JsonPropertyName("flagEffects")]
    public required Dictionary<string, FlagEffectJson> FlagEffects { get; init; }

    [JsonPropertyName("mnemonics")]
    public required Dictionary<string, MnemonicJson> Mnemonics { get; init; }
}

internal sealed class AliasEntryJson
{
    [JsonPropertyName("isAlias")]
    public required bool IsAlias { get; init; }

    [JsonPropertyName("canonicalName")]
    public required string? CanonicalName { get; init; }
}

internal sealed class SpecialRegisterJson
{
    [JsonPropertyName("address")]
    public required int Address { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }
}

internal sealed class FlagEffectJson
{
    [JsonPropertyName("targetFlag")]
    public required string TargetFlag { get; init; }

    [JsonPropertyName("operator")]
    public required string Operator { get; init; }
}

internal sealed class MnemonicJson
{
    [JsonPropertyName("instructionForms")]
    public required List<InstructionFormJson> InstructionForms { get; init; }
}

internal sealed class InstructionFormJson
{
    [JsonPropertyName("isAlias")]
    public required bool IsAlias { get; init; }

    [JsonPropertyName("summary")]
    public required string? Summary { get; init; }

    [JsonPropertyName("allowedFlagEffects")]
    public required List<string> AllowedFlagEffects { get; init; }

    [JsonPropertyName("operands")]
    public required List<OperandJson> Operands { get; init; }

    [JsonPropertyName("writtenRegisters")]
    public required List<string> WrittenRegisters { get; init; }

    [JsonPropertyName("hwStackEffect")]
    public required string HwStackEffect { get; init; }

    [JsonPropertyName("classification")]
    public required ClassificationJson Classification { get; init; }
}

internal sealed class OperandJson
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("bitWidth")]
    public required int BitWidth { get; init; }

    [JsonPropertyName("access")]
    public required string Access { get; init; }

    [JsonPropertyName("supportsImmediate")]
    public required string SupportsImmediate { get; init; }

    [JsonPropertyName("augPrefix")]
    public required string AugPrefix { get; init; }
}

internal sealed class ClassificationJson
{
    [JsonPropertyName("isCall")]
    public required bool IsCall { get; init; }

    [JsonPropertyName("isJump")]
    public required bool IsJump { get; init; }

    [JsonPropertyName("isBranch")]
    public required bool IsBranch { get; init; }

    [JsonPropertyName("isReturn")]
    public required bool IsReturn { get; init; }

    [JsonPropertyName("hasNoRegisterEffect")]
    public required bool HasNoRegisterEffect { get; init; }

    [JsonPropertyName("isPureRegisterLocal")]
    public required bool IsPureRegisterLocal { get; init; }
}
