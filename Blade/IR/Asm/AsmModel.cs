using System.Collections.Generic;

namespace Blade.IR.Asm;

public sealed class AsmModule
{
    public AsmModule(IReadOnlyList<AsmFunction> functions)
    {
        Functions = functions;
    }

    public IReadOnlyList<AsmFunction> Functions { get; }
}

public sealed class AsmFunction
{
    public AsmFunction(string name, bool isEntryPoint, IReadOnlyList<AsmNode> nodes)
    {
        Name = name;
        IsEntryPoint = isEntryPoint;
        Nodes = nodes;
    }

    public string Name { get; }
    public bool IsEntryPoint { get; }
    public IReadOnlyList<AsmNode> Nodes { get; }
}

public abstract class AsmNode
{
}

public sealed class AsmDirectiveNode : AsmNode
{
    public AsmDirectiveNode(string text)
    {
        Text = text;
    }

    public string Text { get; }
}

public sealed class AsmLabelNode : AsmNode
{
    public AsmLabelNode(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

public sealed class AsmInstructionNode : AsmNode
{
    public AsmInstructionNode(string opcode, IReadOnlyList<string> operands, string? predicate)
    {
        Opcode = opcode;
        Operands = operands;
        Predicate = predicate;
    }

    public string Opcode { get; }
    public IReadOnlyList<string> Operands { get; }
    public string? Predicate { get; }
}
