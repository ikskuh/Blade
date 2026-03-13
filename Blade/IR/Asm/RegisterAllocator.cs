using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Blade.IR;

namespace Blade.IR.Asm;

public static class RegisterAllocator
{
    public static AsmModule Allocate(AsmModule module)
    {
        Dictionary<string, Dictionary<int, string>> functionVirtLabels = [];

        foreach (AsmFunction function in module.Functions)
        {
            Dictionary<int, string> virtLabels = [];
            foreach (AsmNode node in function.Nodes)
            {
                switch (node)
                {
                    case AsmInstructionNode instruction:
                        foreach (AsmOperand operand in instruction.Operands)
                            CollectRegisterOperand(operand, virtLabels, function.Name);
                        break;

                    case AsmInlineTextNode inlineText:
                        foreach (AsmOperand operand in inlineText.Bindings.Values)
                            CollectRegisterOperand(operand, virtLabels, function.Name);
                        break;
                }
            }

            functionVirtLabels[function.Name] = virtLabels;
        }

        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
        {
            Dictionary<int, string> virtLabels = functionVirtLabels[function.Name];
            List<AsmNode> rewrittenNodes = new(function.Nodes.Count);

            foreach (AsmNode node in function.Nodes)
            {
                switch (node)
                {
                    case AsmInstructionNode instruction:
                    {
                        List<AsmOperand> operands = new(instruction.Operands.Count);
                        foreach (AsmOperand operand in instruction.Operands)
                            operands.Add(RewriteOperand(operand, virtLabels));
                        rewrittenNodes.Add(new AsmInstructionNode(instruction.Opcode, operands, instruction.Predicate, instruction.FlagEffect));
                        break;
                    }

                    case AsmInlineTextNode inlineText:
                        rewrittenNodes.Add(new AsmInlineTextNode(RewriteInlineAsmText(inlineText.Text, inlineText.Bindings, virtLabels)));
                        break;

                    default:
                        rewrittenNodes.Add(node);
                        break;
                }
            }

            functions.Add(new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, rewrittenNodes));
        }

        if (functions.Count == 0)
            return new AsmModule(module.StoragePlaces, functions);

        int targetIdx = functions.FindIndex(function => function.IsEntryPoint);
        if (targetIdx < 0)
            targetIdx = functions.Count - 1;

        AsmFunction target = functions[targetIdx];
        List<AsmNode> extendedNodes = new(target.Nodes);
        extendedNodes.Add(new AsmCommentNode("--- register file ---"));

        HashSet<string> emitted = [];
        foreach (StoragePlace place in module.StoragePlaces.Where(place => place.Kind == StoragePlaceKind.AllocatableGlobalRegister))
        {
            if (!emitted.Add(place.EmittedName))
                continue;

            extendedNodes.Add(new AsmLabelNode(place.EmittedName));
            extendedNodes.Add(new AsmDirectiveNode($"LONG {FormatStaticInitializer(place.StaticInitializer)}"));
        }

        foreach (AsmFunction function in functions)
        {
            foreach ((int _, string label) in functionVirtLabels[function.Name].OrderBy(entry => entry.Key))
            {
                if (!emitted.Add(label))
                    continue;

                extendedNodes.Add(new AsmLabelNode(label));
                extendedNodes.Add(new AsmDirectiveNode("LONG 0"));
            }
        }

        functions[targetIdx] = new AsmFunction(target.Name, target.IsEntryPoint, target.CcTier, extendedNodes);
        return new AsmModule(module.StoragePlaces, functions);
    }

    private static AsmOperand RewriteOperand(AsmOperand operand, IReadOnlyDictionary<int, string> virtLabels)
    {
        return operand switch
        {
            AsmRegisterOperand reg when virtLabels.TryGetValue(reg.RegisterId, out string? label) => new AsmSymbolOperand(label),
            _ => operand,
        };
    }

    private static string RewriteInlineAsmText(
        string text,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<int, string> virtLabels)
    {
        return Regex.Replace(
            text,
            @"\{(\w+)\}",
            match =>
            {
                string name = match.Groups[1].Value;
                if (!bindings.TryGetValue(name, out AsmOperand? operand))
                    return name;

                AsmOperand rewritten = RewriteOperand(operand, virtLabels);
                return rewritten switch
                {
                    AsmSymbolOperand symbol => symbol.Name,
                    AsmPlaceOperand place => place.Place.EmittedName,
                    _ => rewritten.Format(),
                };
            });
    }

    private static void CollectRegisterOperand(
        AsmOperand operand,
        IDictionary<int, string> virtLabels,
        string functionName)
    {
        if (operand is AsmRegisterOperand reg && !virtLabels.ContainsKey(reg.RegisterId))
            virtLabels[reg.RegisterId] = SanitizeLabel($"{functionName}_r{reg.RegisterId}");
    }

    private static string SanitizeLabel(string name)
    {
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
                chars[i] = '_';
        }

        return new string(chars);
    }

    private static string FormatStaticInitializer(object? value)
    {
        return value switch
        {
            null => "0",
            bool boolean => boolean ? "1" : "0",
            _ => value.ToString() ?? "0",
        };
    }
}
