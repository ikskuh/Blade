using System.Globalization;
using System.Text;

namespace Blade.P2MetadataGen;

internal static class MetadataCodeGenerator
{
    private static readonly string[] OperandAccessNames = ["None", "Read", "Write", "ReadWrite"];
    private static readonly string[] OperandRoleNames = ["ADDR", "address register", "D", "MODCZ", "N", "S"];
    private static readonly string[] OperandTypeNames = ["bit", "bitrange", "branch target", "hub ptrexpr", "lut ptrexpr", "modcz", "pin", "pinrange", "regular"];
    private static readonly string[] ImmediateSupportNames = ["no", "optional", "required"];
    private static readonly string[] AugPrefixNames = ["AUGD", "AUGS", "None"];
    private static readonly string[] HwStackEffectNames = ["None", "Pop", "Push"];
    private static readonly string[] FlagNameNames = ["both", "c", "none", "z"];
    private static readonly string[] FlagOperatorNames = ["and", "none", "or", "set", "xor"];

    public static void Validate(MetadataJsonRoot model)
    {
        ArgumentNullException.ThrowIfNull(model);

        ValidateAliasEntries(model.ConditionCodes, "conditionCodes");
        ValidateAliasEntries(model.ModczOperands, "modczOperands");
        ValidateSpecialRegisters(model.SpecialRegisters);
        ValidateFlagEffects(model.FlagEffects);
        ValidateMnemonics(model);
    }

    public static string GenerateSource(MetadataJsonRoot model, string inputPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        OrderedSets orderedSets = BuildOrderedSets(model);
        GeneratedLookupSets lookupSets = BuildLookupSets(model);
        string relativeInputPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(inputPath)).Replace('\\', '/');

        StringBuilder writer = new();
        AppendHeader(writer, relativeInputPath);
        AppendEnums(writer, orderedSets);
        AppendRecordTypes(writer);
        AppendConditionCodeExtensions(writer, model, orderedSets);
        AppendModczExtensions(writer, model, orderedSets);
        AppendFlagEffectExtensions(writer, model, orderedSets);
        AppendOperandAccessExtensions(writer);
        AppendImmediateSupportExtensions(writer);
        AppendSpecialRegisterExtensions(writer, model, orderedSets);
        AppendMnemonicExtensions(writer, model, orderedSets, lookupSets);
        writer.AppendLine("#nullable restore");

        return writer.ToString();
    }

    private static void ValidateAliasEntries(Dictionary<string, AliasEntryJson> entries, string sectionName)
    {
        if (entries.Count == 0)
            throw new FormatException($"Section '{sectionName}' must not be empty.");

        foreach ((string key, AliasEntryJson value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new FormatException($"Section '{sectionName}' contains an empty key.");

            if (value.IsAlias)
            {
                if (string.IsNullOrWhiteSpace(value.CanonicalName))
                    throw new FormatException($"Alias '{key}' in '{sectionName}' must define canonicalName.");

                if (!entries.TryGetValue(value.CanonicalName, out AliasEntryJson? canonical))
                    throw new FormatException($"Alias '{key}' in '{sectionName}' references missing canonicalName '{value.CanonicalName}'.");

                if (canonical.IsAlias)
                    throw new FormatException($"Alias '{key}' in '{sectionName}' references alias '{value.CanonicalName}' instead of a canonical entry.");
            }
            else if (value.CanonicalName is not null)
            {
                throw new FormatException($"Canonical entry '{key}' in '{sectionName}' must not define canonicalName.");
            }
        }
    }

    private static void ValidateSpecialRegisters(Dictionary<string, SpecialRegisterJson> specialRegisters)
    {
        if (specialRegisters.Count == 0)
            throw new FormatException("Section 'specialRegisters' must not be empty.");

        HashSet<int> seenAddresses = [];
        foreach ((string key, SpecialRegisterJson value) in specialRegisters)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new FormatException("Section 'specialRegisters' contains an empty key.");

            if (!seenAddresses.Add(value.Address))
                throw new FormatException($"Special register '{key}' uses duplicate address {value.Address.ToString(CultureInfo.InvariantCulture)}.");

            if (string.IsNullOrWhiteSpace(value.Description))
                throw new FormatException($"Special register '{key}' must define a description.");
        }
    }

    private static void ValidateFlagEffects(Dictionary<string, FlagEffectJson> flagEffects)
    {
        if (flagEffects.Count == 0)
            throw new FormatException("Section 'flagEffects' must not be empty.");

        foreach ((string key, FlagEffectJson value) in flagEffects)
        {
            if (!FlagNameNames.Contains(value.TargetFlag, StringComparer.Ordinal))
                throw new FormatException($"Flag effect '{key}' uses unsupported targetFlag '{value.TargetFlag}'.");

            if (!FlagOperatorNames.Contains(value.Operator, StringComparer.Ordinal))
                throw new FormatException($"Flag effect '{key}' uses unsupported operator '{value.Operator}'.");
        }
    }

    private static void ValidateMnemonics(MetadataJsonRoot model)
    {
        if (model.Mnemonics.Count == 0)
            throw new FormatException("Section 'mnemonics' must not be empty.");

        HashSet<string> flagEffects = model.FlagEffects.Keys.ToHashSet(StringComparer.Ordinal);
        HashSet<string> writtenRegisters = ["D"];
        foreach (string specialRegisterName in model.SpecialRegisters.Keys)
            writtenRegisters.Add(specialRegisterName);

        foreach ((string mnemonicName, MnemonicJson mnemonic) in model.Mnemonics)
        {
            if (mnemonic.InstructionForms.Count == 0)
                throw new FormatException($"Mnemonic '{mnemonicName}' must define at least one instruction form.");

            foreach (InstructionFormJson form in mnemonic.InstructionForms)
            {
                if (!HwStackEffectNames.Contains(form.HwStackEffect, StringComparer.Ordinal))
                    throw new FormatException($"Mnemonic '{mnemonicName}' uses unsupported hwStackEffect '{form.HwStackEffect}'.");

                foreach (string effectName in form.AllowedFlagEffects)
                {
                    if (!flagEffects.Contains(effectName))
                        throw new FormatException($"Mnemonic '{mnemonicName}' references unknown flag effect '{effectName}'.");
                }

                foreach (string registerName in form.WrittenRegisters)
                {
                    if (!writtenRegisters.Contains(registerName))
                        throw new FormatException($"Mnemonic '{mnemonicName}' references unknown written register '{registerName}'.");
                }

                foreach (OperandJson operand in form.Operands)
                {
                    ValidateOperand(mnemonicName, operand);
                }

                ValidateClassification(mnemonicName, form.Classification);
            }
        }
    }

    private static void ValidateOperand(string mnemonicName, OperandJson operand)
    {
        if (!OperandRoleNames.Contains(operand.Role, StringComparer.Ordinal))
            throw new FormatException($"Mnemonic '{mnemonicName}' uses unsupported operand role '{operand.Role}'.");

        if (!OperandTypeNames.Contains(operand.Type, StringComparer.Ordinal))
            throw new FormatException($"Mnemonic '{mnemonicName}' uses unsupported operand type '{operand.Type}'.");

        if (!OperandAccessNames.Contains(operand.Access, StringComparer.Ordinal))
            throw new FormatException($"Mnemonic '{mnemonicName}' uses unsupported operand access '{operand.Access}'.");

        if (!ImmediateSupportNames.Contains(operand.SupportsImmediate, StringComparer.Ordinal))
            throw new FormatException($"Mnemonic '{mnemonicName}' uses unsupported immediate support '{operand.SupportsImmediate}'.");

        if (!AugPrefixNames.Contains(operand.AugPrefix, StringComparer.Ordinal))
            throw new FormatException($"Mnemonic '{mnemonicName}' uses unsupported augPrefix '{operand.AugPrefix}'.");

        if (operand.BitWidth < 0)
            throw new FormatException($"Mnemonic '{mnemonicName}' uses negative bitWidth {operand.BitWidth.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static void ValidateClassification(string mnemonicName, ClassificationJson classification)
    {
        int controlFlowCount = CountTrue(classification.IsCall, classification.IsJump, classification.IsBranch, classification.IsReturn);
        if (controlFlowCount > 1)
        {
            throw new FormatException(
                $"Mnemonic '{mnemonicName}' form has conflicting control-flow classification flags.");
        }
    }

    private static int CountTrue(params bool[] values)
    {
        int count = 0;
        foreach (bool value in values)
        {
            if (value)
                count++;
        }

        return count;
    }

    private static OrderedSets BuildOrderedSets(MetadataJsonRoot model)
    {
        return new OrderedSets(
            ConditionCodes: model.ConditionCodes.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            ModczOperands: model.ModczOperands.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            SpecialRegisters: model.SpecialRegisters.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            FlagEffects: model.FlagEffects.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            Mnemonics: model.Mnemonics.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray());
    }

    private static GeneratedLookupSets BuildLookupSets(MetadataJsonRoot model)
    {
        Dictionary<string, string> flagSets = new(StringComparer.Ordinal);
        Dictionary<string, string> registerSets = new(StringComparer.Ordinal);
        int flagIndex = 0;
        int registerIndex = 0;

        foreach ((string mnemonicName, MnemonicJson mnemonic) in model.Mnemonics.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (InstructionFormJson form in mnemonic.InstructionForms.OrderBy(static item => item.Operands.Count))
            {
                string flagKey = string.Join("|", form.AllowedFlagEffects.OrderBy(static item => item, StringComparer.Ordinal));
                if (!flagSets.ContainsKey(flagKey))
                {
                    flagSets.Add(flagKey, "AllowedFlagEffectsSet" + flagIndex.ToString(CultureInfo.InvariantCulture));
                    flagIndex++;
                }

                string registerKey = string.Join("|", form.WrittenRegisters.OrderBy(static item => item, StringComparer.Ordinal));
                if (!registerSets.ContainsKey(registerKey))
                {
                    registerSets.Add(registerKey, "WrittenRegistersSet" + registerIndex.ToString(CultureInfo.InvariantCulture));
                    registerIndex++;
                }
            }
        }

        return new GeneratedLookupSets(flagSets, registerSets);
    }

    private static void AppendHeader(StringBuilder writer, string relativeInputPath)
    {
        writer.AppendLine("// <auto-generated>");
        writer.AppendLine("// Generated from " + relativeInputPath + ".");
        writer.AppendLine("// Generated by Tools/Blade.P2MetadataGen.");
        writer.AppendLine("// Do not edit this file manually.");
        writer.AppendLine("// </auto-generated>");
        writer.AppendLine();
        writer.AppendLine("#nullable enable");
        writer.AppendLine();
        writer.AppendLine("using System;");
        writer.AppendLine("using System.Collections.Frozen;");
        writer.AppendLine("using System.Collections.Generic;");
        writer.AppendLine("using System.Linq;");
        writer.AppendLine();
        writer.AppendLine("namespace Blade;");
        writer.AppendLine();
    }

    private static void AppendEnums(StringBuilder writer, OrderedSets orderedSets)
    {
        AppendSimpleEnum(writer, "P2OperandAccess", OperandAccessNames.Select(ToPascalCase));
        AppendSimpleEnum(writer, "P2OperandRole", OperandRoleNames.Select(ToRoleIdentifier));
        AppendSimpleEnum(writer, "P2OperandType", OperandTypeNames.Select(ToPascalCase));
        AppendSimpleEnum(writer, "P2ImmediateSupport", ImmediateSupportNames.Select(ToPascalCase));
        AppendSimpleEnum(writer, "P2AugPrefixKind", AugPrefixNames);
        AppendSimpleEnum(writer, "P2HwStackEffect", HwStackEffectNames);
        AppendSimpleEnum(writer, "P2FlagName", FlagNameNames.Select(ToPascalCase));
        AppendSimpleEnum(writer, "P2FlagOperator", FlagOperatorNames.Select(ToPascalCase));
        AppendSimpleEnum(writer, "P2FlagEffect", orderedSets.FlagEffects.Select(ToFlagEffectIdentifier));
        AppendSimpleEnum(writer, "P2WrittenRegister", ["D", .. orderedSets.SpecialRegisters]);
        AppendSimpleEnum(writer, "P2Mnemonic", orderedSets.Mnemonics.Select(ToEnumIdentifier));
        AppendSimpleEnum(writer, "P2ConditionCode", orderedSets.ConditionCodes.Select(ToConditionCodeIdentifier));
        AppendSimpleEnum(writer, "P2ModczOperand", orderedSets.ModczOperands.Select(ToEnumIdentifier));
    }

    private static void AppendSimpleEnum(StringBuilder writer, string enumName, IEnumerable<string> members)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Generated metadata enumeration for " + enumName + ".");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public enum " + enumName);
        writer.AppendLine("{");
        foreach (string member in members)
        {
            writer.AppendLine("    " + member + ",");
        }

        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendRecordTypes(StringBuilder writer)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Describes a single instruction operand in generated Propeller 2 metadata.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public readonly record struct P2InstructionOperandInfo(");
        writer.AppendLine("    P2OperandRole Role,");
        writer.AppendLine("    P2OperandType Type,");
        writer.AppendLine("    int BitWidth,");
        writer.AppendLine("    P2OperandAccess Access,");
        writer.AppendLine("    P2ImmediateSupport SupportsImmediate,");
        writer.AppendLine("    P2AugPrefixKind AugPrefix);");
        writer.AppendLine();
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Describes a single instruction form in generated Propeller 2 metadata.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public sealed record P2InstructionFormInfo(");
        writer.AppendLine("    bool IsAlias,");
        writer.AppendLine("    string? Summary,");
        writer.AppendLine("    IReadOnlyList<P2InstructionOperandInfo> Operands,");
        writer.AppendLine("    IReadOnlySet<P2WrittenRegister> WrittenRegisters,");
        writer.AppendLine("    IReadOnlySet<P2FlagEffect> AllowedFlagEffects,");
        writer.AppendLine("    P2HwStackEffect HwStackEffect,");
        writer.AppendLine("    bool IsCall,");
        writer.AppendLine("    bool IsJump,");
        writer.AppendLine("    bool IsBranch,");
        writer.AppendLine("    bool IsReturn,");
        writer.AppendLine("    bool HasNoRegisterEffect,");
        writer.AppendLine("    bool IsPureRegisterLocal)");
        writer.AppendLine("{");
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets whether the instruction form transfers control flow.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public bool IsControlFlow => IsCall || IsJump || IsBranch || IsReturn;");
        writer.AppendLine("}");
        writer.AppendLine();
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Groups all generated instruction forms that belong to one mnemonic.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public sealed record class P2MnemonicInfo(");
        writer.AppendLine("    P2Mnemonic Mnemonic,");
        writer.AppendLine("    IReadOnlyCollection<P2InstructionFormInfo> Forms)");
        writer.AppendLine("{");
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets the instruction forms that use the specified operand count.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public IReadOnlyCollection<P2InstructionFormInfo> GetFormsByOperandCount(int count)");
        writer.AppendLine("    {");
        writer.AppendLine("        if (count < 0)");
        writer.AppendLine("            return [];");
        writer.AppendLine();
        writer.AppendLine("        return Forms.Where(form => form.Operands.Count == count).ToArray();");
        writer.AppendLine("    }");
        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendConditionCodeExtensions(StringBuilder writer, MetadataJsonRoot model, OrderedSets orderedSets)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Extension methods and parse helpers for <see cref=\"P2ConditionCode\"/>.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public static class P2ConditionCodeExtensions");
        writer.AppendLine("{");
        AppendStringArray(writer, "Texts", orderedSets.ConditionCodes.Select(RenderStringLiteral));
        AppendEnumArray(
            writer,
            "CanonicalCodes",
            "P2ConditionCode",
            orderedSets.ConditionCodes.Select(
                name =>
                {
                    AliasEntryJson entry = model.ConditionCodes[name];
                    string canonical = entry.CanonicalName ?? name;
                    return "P2ConditionCode." + ToConditionCodeIdentifier(canonical);
                }));
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets the canonical condition code for the provided value.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static P2ConditionCode GetCanonicalName(this P2ConditionCode code)");
        writer.AppendLine("        => CanonicalCodes[GetIndex(code)];");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets the text representation of the condition code.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static string GetText(this P2ConditionCode code)");
        writer.AppendLine("        => Texts[GetIndex(code)];");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets whether the condition code is an alias.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static bool IsAlias(this P2ConditionCode code)");
        writer.AppendLine("        => CanonicalCodes[GetIndex(code)] != code;");
        AppendEnumIndexHelper(writer, "P2ConditionCode", "Texts.Length");
        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendModczExtensions(StringBuilder writer, MetadataJsonRoot model, OrderedSets orderedSets)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Extension methods and parse helpers for <see cref=\"P2ModczOperand\"/>.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public static class P2ModczOperandExtensions");
        writer.AppendLine("{");
        AppendStringArray(writer, "Texts", orderedSets.ModczOperands.Select(RenderStringLiteral));
        AppendEnumArray(
            writer,
            "CanonicalOperands",
            "P2ModczOperand",
            orderedSets.ModczOperands.Select(
                name =>
                {
                    AliasEntryJson entry = model.ModczOperands[name];
                    string canonical = entry.CanonicalName ?? name;
                    return "P2ModczOperand." + ToEnumIdentifier(canonical);
                }));
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets the canonical MODCZ operand for the provided value.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static P2ModczOperand GetCanonicalName(this P2ModczOperand operand)");
        writer.AppendLine("        => CanonicalOperands[GetIndex(operand)];");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets whether the MODCZ operand is an alias.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static bool IsAlias(this P2ModczOperand operand)");
        writer.AppendLine("        => CanonicalOperands[GetIndex(operand)] != operand;");
        AppendEnumIndexHelper(writer, "P2ModczOperand", "Texts.Length");
        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendFlagEffectExtensions(StringBuilder writer, MetadataJsonRoot model, OrderedSets orderedSets)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Extension methods and parse helpers for <see cref=\"P2FlagEffect\"/>.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public static class P2FlagEffectExtensions");
        writer.AppendLine("{");
        AppendEnumArray(
            writer,
            "FlagNames",
            "P2FlagName",
            orderedSets.FlagEffects.Select(name => "P2FlagName." + ToPascalCase(model.FlagEffects[name].TargetFlag)));
        AppendEnumArray(
            writer,
            "FlagOperators",
            "P2FlagOperator",
            orderedSets.FlagEffects.Select(name => "P2FlagOperator." + ToPascalCase(model.FlagEffects[name].Operator)));
        AppendParseDictionary(
            writer,
            "ByText",
            "P2FlagEffect",
            orderedSets.FlagEffects.Select(name => (name, "P2FlagEffect." + ToFlagEffectIdentifier(name))));

        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Parses a flag-effect token.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static bool TryParse(string text, out P2FlagEffect effect)");
        writer.AppendLine("        => ByText.TryGetValue(text, out effect);");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets the target flag for the provided effect.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static P2FlagName GetFlagName(this P2FlagEffect effect)");
        writer.AppendLine("        => FlagNames[GetIndex(effect)];");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets the flag operator for the provided effect.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static P2FlagOperator GetOperator(this P2FlagEffect effect)");
        writer.AppendLine("        => FlagOperators[GetIndex(effect)];");
        AppendEnumIndexHelper(writer, "P2FlagEffect", "FlagNames.Length");
        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendOperandAccessExtensions(StringBuilder writer)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Extension methods for <see cref=\"P2OperandAccess\"/>.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public static class P2OperandAccessExtensions");
        writer.AppendLine("{");
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets whether the operand access includes a read.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static bool IsReading(this P2OperandAccess access)");
        writer.AppendLine("        => access is P2OperandAccess.Read or P2OperandAccess.ReadWrite;");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets whether the operand access includes a write.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static bool IsWriting(this P2OperandAccess access)");
        writer.AppendLine("        => access is P2OperandAccess.Write or P2OperandAccess.ReadWrite;");
        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendImmediateSupportExtensions(StringBuilder writer)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Extension methods for <see cref=\"P2ImmediateSupport\"/>.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public static class P2ImmediateSupportExtensions");
        writer.AppendLine("{");
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets whether the operand supports immediate syntax.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static bool SupportsImmediate(this P2ImmediateSupport support)");
        writer.AppendLine("        => support is P2ImmediateSupport.Optional or P2ImmediateSupport.Required;");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets whether the operand requires immediate syntax.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static bool RequiresImmediate(this P2ImmediateSupport support)");
        writer.AppendLine("        => support == P2ImmediateSupport.Required;");
        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendSpecialRegisterExtensions(StringBuilder writer, MetadataJsonRoot model, OrderedSets orderedSets)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Extension methods and parse helpers for <see cref=\"P2SpecialRegister\"/>.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public static class P2SpecialRegisterExtensions");
        writer.AppendLine("{");
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets the text representation of the special register.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static string GetText(this P2SpecialRegister register)");
        writer.AppendLine("        => register switch");
        writer.AppendLine("        {");
        foreach (string name in orderedSets.SpecialRegisters)
            writer.AppendLine("            P2SpecialRegister." + ToEnumIdentifier(name) + " => " + RenderStringLiteral(name) + ",");
        writer.AppendLine("            _ => throw new ArgumentOutOfRangeException(nameof(register), register, \"Unknown P2SpecialRegister value.\"),");
        writer.AppendLine("        };");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets the description of the special register.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static string GetDescription(this P2SpecialRegister register)");
        writer.AppendLine("        => register switch");
        writer.AppendLine("        {");
        foreach (string name in orderedSets.SpecialRegisters)
            writer.AppendLine("            P2SpecialRegister." + ToEnumIdentifier(name) + " => " + RenderStringLiteral(model.SpecialRegisters[name].Description) + ",");
        writer.AppendLine("            _ => throw new ArgumentOutOfRangeException(nameof(register), register, \"Unknown P2SpecialRegister value.\"),");
        writer.AppendLine("        };");
        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendMnemonicExtensions(StringBuilder writer, MetadataJsonRoot model, OrderedSets orderedSets, GeneratedLookupSets lookupSets)
    {
        writer.AppendLine("/// <summary>");
        writer.AppendLine("/// Extension methods and parse helpers for <see cref=\"P2Mnemonic\"/>.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("public static class P2MnemonicExtensions");
        writer.AppendLine("{");

        AppendLookupSetFields(writer, model, orderedSets, lookupSets);
        AppendMnemonicFormTables(writer, model, orderedSets, lookupSets);
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets all instruction forms for the mnemonic.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static IReadOnlyCollection<P2InstructionFormInfo> GetInstructionForms(this P2Mnemonic mnemonic)");
        writer.AppendLine("        => MnemonicInfos[GetIndex(mnemonic)].Forms;");
        writer.AppendLine();
        writer.AppendLine("    /// <summary>");
        writer.AppendLine("    /// Gets all instruction forms for the mnemonic that use the specified operand count.");
        writer.AppendLine("    /// </summary>");
        writer.AppendLine("    public static IReadOnlyCollection<P2InstructionFormInfo> GetInstructionForms(this P2Mnemonic mnemonic, int operandCount)");
        writer.AppendLine("        => MnemonicInfos[GetIndex(mnemonic)].GetFormsByOperandCount(operandCount);");
        AppendEnumIndexHelper(writer, "P2Mnemonic", "MnemonicInfos.Length");
        writer.AppendLine("}");
        writer.AppendLine();
    }

    private static void AppendLookupSetFields(StringBuilder writer, MetadataJsonRoot model, OrderedSets orderedSets, GeneratedLookupSets lookupSets)
    {
        foreach ((string key, string fieldName) in lookupSets.AllowedFlagEffectSets.OrderBy(static pair => pair.Value, StringComparer.Ordinal))
        {
            string[] effectNames = key.Length == 0
                ? []
                : key.Split('|', StringSplitOptions.RemoveEmptyEntries);

            writer.AppendLine("    private static readonly IReadOnlySet<P2FlagEffect> " + fieldName + " = new P2FlagEffect[]");
            writer.AppendLine("    {");
            foreach (string effectName in effectNames)
                writer.AppendLine("        P2FlagEffect." + ToFlagEffectIdentifier(effectName) + ",");
            writer.AppendLine("    }.ToFrozenSet();");
            writer.AppendLine();
        }

        foreach ((string key, string fieldName) in lookupSets.WrittenRegisterSets.OrderBy(static pair => pair.Value, StringComparer.Ordinal))
        {
            string[] registerNames = key.Length == 0
                ? []
                : key.Split('|', StringSplitOptions.RemoveEmptyEntries);

            writer.AppendLine("    private static readonly IReadOnlySet<P2WrittenRegister> " + fieldName + " = new P2WrittenRegister[]");
            writer.AppendLine("    {");
            foreach (string registerName in registerNames)
                writer.AppendLine("        P2WrittenRegister." + ToEnumIdentifier(registerName) + ",");
            writer.AppendLine("    }.ToFrozenSet();");
            writer.AppendLine();
        }
    }

    private static void AppendMnemonicFormTables(StringBuilder writer, MetadataJsonRoot model, OrderedSets orderedSets, GeneratedLookupSets lookupSets)
    {
        writer.AppendLine("    private static readonly P2MnemonicInfo[] MnemonicInfos =");
        writer.AppendLine("    [");
        foreach (string mnemonicName in orderedSets.Mnemonics)
        {
            MnemonicJson mnemonic = model.Mnemonics[mnemonicName];
            writer.AppendLine("        new P2MnemonicInfo(");
            writer.AppendLine("            Mnemonic: P2Mnemonic." + ToEnumIdentifier(mnemonicName) + ",");
            writer.AppendLine("            Forms: new P2InstructionFormInfo[]");
            writer.AppendLine("        {");
            foreach (InstructionFormJson form in mnemonic.InstructionForms.OrderBy(static item => item.Operands.Count))
                writer.AppendLine("                " + RenderFormInitializer(form, lookupSets) + ",");
            writer.AppendLine("            }),");
        }
        writer.AppendLine("    ];");
        writer.AppendLine();
    }

    private static string RenderFormInitializer(InstructionFormJson form, GeneratedLookupSets lookupSets)
    {
        string flagKey = string.Join("|", form.AllowedFlagEffects.OrderBy(static item => item, StringComparer.Ordinal));
        string registerKey = string.Join("|", form.WrittenRegisters.OrderBy(static item => item, StringComparer.Ordinal));

        StringBuilder builder = new();
        builder.Append("new P2InstructionFormInfo(");
        builder.Append("IsAlias: ").Append(RenderBool(form.IsAlias)).Append(", ");
        builder.Append("Summary: ").Append(form.Summary is null ? "null" : RenderStringLiteral(form.Summary)).Append(", ");
        builder.Append("Operands: new P2InstructionOperandInfo[] { ");
        for (int i = 0; i < form.Operands.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            builder.Append(RenderOperandInitializer(form.Operands[i]));
        }

        builder.Append(" }, ");
        builder.Append("WrittenRegisters: ").Append(lookupSets.WrittenRegisterSets[registerKey]).Append(", ");
        builder.Append("AllowedFlagEffects: ").Append(lookupSets.AllowedFlagEffectSets[flagKey]).Append(", ");
        builder.Append("HwStackEffect: P2HwStackEffect.").Append(ToPascalCase(form.HwStackEffect)).Append(", ");
        builder.Append("IsCall: ").Append(RenderBool(form.Classification.IsCall)).Append(", ");
        builder.Append("IsJump: ").Append(RenderBool(form.Classification.IsJump)).Append(", ");
        builder.Append("IsBranch: ").Append(RenderBool(form.Classification.IsBranch)).Append(", ");
        builder.Append("IsReturn: ").Append(RenderBool(form.Classification.IsReturn)).Append(", ");
        builder.Append("HasNoRegisterEffect: ").Append(RenderBool(form.Classification.HasNoRegisterEffect)).Append(", ");
        builder.Append("IsPureRegisterLocal: ").Append(RenderBool(form.Classification.IsPureRegisterLocal)).Append(')');
        return builder.ToString();
    }

    private static string RenderOperandInitializer(OperandJson operand)
    {
        return "new P2InstructionOperandInfo("
            + "Role: P2OperandRole." + ToRoleIdentifier(operand.Role) + ", "
            + "Type: P2OperandType." + ToPascalCase(operand.Type) + ", "
            + "BitWidth: " + operand.BitWidth.ToString(CultureInfo.InvariantCulture) + ", "
            + "Access: P2OperandAccess." + ToPascalCase(operand.Access) + ", "
            + "SupportsImmediate: P2ImmediateSupport." + ToPascalCase(operand.SupportsImmediate) + ", "
            + "AugPrefix: P2AugPrefixKind." + operand.AugPrefix + ")";
    }

    private static void AppendStringArray(StringBuilder writer, string name, IEnumerable<string> items)
    {
        writer.AppendLine("    private static readonly string[] " + name + " =");
        writer.AppendLine("    [");
        foreach (string item in items)
            writer.AppendLine("        " + item + ",");
        writer.AppendLine("    ];");
        writer.AppendLine();
    }

    private static void AppendBoolArray(StringBuilder writer, string name, IEnumerable<bool> items)
    {
        writer.AppendLine("    private static readonly bool[] " + name + " =");
        writer.AppendLine("    [");
        foreach (bool item in items)
            writer.AppendLine("        " + RenderBool(item) + ",");
        writer.AppendLine("    ];");
        writer.AppendLine();
    }

    private static void AppendEnumArray(StringBuilder writer, string name, string typeName, IEnumerable<string> items)
    {
        writer.AppendLine("    private static readonly " + typeName + "[] " + name + " =");
        writer.AppendLine("    [");
        foreach (string item in items)
            writer.AppendLine("        " + item + ",");
        writer.AppendLine("    ];");
        writer.AppendLine();
    }

    private static void AppendParseDictionary(StringBuilder writer, string name, string typeName, IEnumerable<(string Text, string Value)> items)
    {
        writer.AppendLine("    private static readonly FrozenDictionary<string, " + typeName + "> " + name + " =");
        writer.AppendLine("        new Dictionary<string, " + typeName + ">(StringComparer.OrdinalIgnoreCase)");
        writer.AppendLine("        {");
        foreach ((string text, string value) in items)
            writer.AppendLine("            [" + RenderStringLiteral(text) + "] = " + value + ",");
        writer.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        writer.AppendLine();
    }

    private static void AppendEnumIndexHelper(StringBuilder writer, string typeName, string countExpression)
    {
        writer.AppendLine();
        writer.AppendLine("    private static int GetIndex(" + typeName + " value)");
        writer.AppendLine("    {");
        writer.AppendLine("        int index = (int)value;");
        writer.AppendLine("        if ((uint)index >= (uint)" + countExpression + ")");
        writer.AppendLine("            throw new ArgumentOutOfRangeException(nameof(value), value, \"Unknown " + typeName + " value.\");");
        writer.AppendLine();
        writer.AppendLine("        return index;");
        writer.AppendLine("    }");
    }

    private static string ToPascalCase(string value)
    {
        if (value.Length == 0)
            return value;

        string[] parts = value.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        StringBuilder builder = new();
        foreach (string part in parts)
        {
            if (part.Length == 0)
                continue;

            if (part.All(static ch => char.IsUpper(ch) || char.IsDigit(ch)))
            {
                builder.Append(part);
                continue;
            }

            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
                builder.Append(part.AsSpan(1));
        }

        return builder.ToString();
    }

    private static string ToRoleIdentifier(string value)
    {
        return value switch
        {
            "ADDR" => "ADDR",
            "address register" => "AddressRegister",
            "MODCZ" => "MODCZ",
            _ => ToPascalCase(value),
        };
    }

    private static string ToConditionCodeIdentifier(string value)
    {
        return value == "<INST>" ? "INST" : ToEnumIdentifier(value);
    }

    private static string ToFlagEffectIdentifier(string value)
    {
        return value == "none" ? "None" : ToEnumIdentifier(value);
    }

    private static string ToEnumIdentifier(string value)
    {
        return value.Replace("<", string.Empty, StringComparison.Ordinal)
            .Replace(">", string.Empty, StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal)
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
    }

    private static string RenderBool(bool value)
        => value ? "true" : "false";

    private static string RenderStringLiteral(string value)
    {
        StringBuilder builder = new("\"");
        foreach (char ch in value)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => null,
            });

            if (ch is not ('\\' or '"' or '\r' or '\n' or '\t'))
                builder.Append(ch);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private sealed record OrderedSets(
        string[] ConditionCodes,
        string[] ModczOperands,
        string[] SpecialRegisters,
        string[] FlagEffects,
        string[] Mnemonics);

    private sealed record GeneratedLookupSets(
        Dictionary<string, string> AllowedFlagEffectSets,
        Dictionary<string, string> WrittenRegisterSets);
}
