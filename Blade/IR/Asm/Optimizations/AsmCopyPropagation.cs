using System.Collections.Generic;
using static Blade.IR.Asm.AsmOptimizationHelpers;

namespace Blade.IR.Asm.Optimizations;

[AsmOptimization("copy-prop", Priority = 900)]
public sealed class AsmCopyPropagation : PerFunctionAsmOptimization
{
    protected override AsmFunction? RunOnFunction(AsmFunction input)
    {
        Dictionary<VirtualAsmRegister, AsmOperand> aliases = [];
        List<AsmNode> nodes = [];
        bool changed = false;

        foreach (AsmNode node in input.Nodes)
        {
            switch (node)
            {
                case AsmCommentNode:
                    nodes.Add(node);
                    break;

                case AsmImplicitUseNode implicitUse:
                    nodes.Add(RewriteImplicitUse(implicitUse, aliases));
                    aliases.Clear();
                    break;

                case AsmLabelNode:
                case AsmDirectiveNode:
                case AsmVolatileRegionBeginNode:
                case AsmVolatileRegionEndNode:
                    aliases.Clear();
                    nodes.Add(node);
                    break;

                case AsmInstructionNode instruction when IsBarrier(instruction):
                    aliases.Clear();
                    nodes.Add(instruction);
                    break;

                case AsmInstructionNode instruction:
                {
                    AsmInstructionNode rewritten = RewriteInstructionSources(instruction, aliases);
                    changed |= !ReferenceEquals(rewritten, instruction);

                    InvalidateAliases(rewritten, aliases);
                    if (TryGetTrackedCopy(rewritten, out AsmRegisterOperand dest, out AsmOperand source)
                        && !OperandsEquivalent(dest, source))
                    {
                        aliases[dest.Register] = ResolveAlias(source, aliases);
                    }
                    nodes.Add(rewritten);
                    break;
                }

                default:
                    nodes.Add(node);
                    break;
            }
        }

        return changed
            ? new AsmFunction(input, nodes)
            : null;
    }
}
