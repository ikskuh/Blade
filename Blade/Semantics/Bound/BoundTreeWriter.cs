using System;
using System.Globalization;
using System.IO;
using System.Text;
using Blade.Reports;

using static Blade.Reports.BasicTextSpanKind;
using static Blade.Reports.SemanticTextSpanKind;

namespace Blade.Semantics.Bound;

/// <summary>
/// Emits the bound tree as a readable hierarchical dump.
/// </summary>
public static class BoundTreeWriter
{
    /// <summary>
    /// Emits the supplied bound program into the provided report builder.
    /// </summary>
    public static void Write(ITextReportBuilder builder, BoundProgram program)
    {
        Requires.NotNull(builder);
        Requires.NotNull(program);

        Writer writer = new(builder);
        writer.WriteProgram(program);
    }

    private sealed class Writer(ITextReportBuilder builder) : TextReportBuilderBase(builder)
    {
        public void WriteProgram(BoundProgram program)
        {
            WriteIndentedLine(0, (Keyword, "Program"));

            WriteIndentedLine(1, (Keyword, "Globals"));
            foreach (GlobalVariableSymbol global in program.GlobalVariables)
            {
                Append(Space(2 * 2), (VariableName, global, global.Name), ':', ' ');
                AppendType(global.Type);
                NewLine();
                if (global.Initializer is not null)
                    WriteExpression(3, global.Initializer);
            }

            WriteIndentedLine(1, (Keyword, "EntryPoint"), ' ', (FunctionName, program.EntryPoint, program.EntryPoint.Name), ' ', '(', (Literal, program.EntryPoint.StorageClass.ToString()), ')');
            WriteStatement(2, program.EntryPointFunction.Body);

            WriteIndentedLine(1, (Keyword, "Functions"));
            foreach (BoundFunctionMember function in program.Functions)
            {
                if (ReferenceEquals(function, program.EntryPointFunction))
                    continue;

                WriteIndentedLine(2, (FunctionName, function.Symbol, function.Symbol.Name), ' ', '(', (Literal, function.Symbol.Kind.ToString()), ')');
                WriteStatement(3, function.Body);
            }

            WriteIndentedLine(1, (Keyword, "Modules"));
            foreach (BoundModule module in program.Modules)
            {
                WriteIndentedLine(2, (Comment, module.ResolvedFilePath));
                WriteIndentedLine(3, (Keyword, "Exports"));
                foreach ((string name, Symbol symbol) in module.ExportedSymbols)
                    WriteExport(4, name, symbol);
            }
        }

        private void WriteExport(int indent, string name, Symbol symbol)
        {
            Requires.NotNull(name);
            Requires.NotNull(symbol);

            Append(Space(indent * 2), (Comment, name), ':', ' ');
            switch (symbol)
            {
                case TypeSymbol typeSymbol:
                    Append((Keyword, "type"), ' ');
                    AppendType(typeSymbol.Type);
                    break;

                case FunctionSymbol functionSymbol:
                    Append((Keyword, "function"), ' ', (Literal, functionSymbol.Kind.ToString()));
                    break;

                case GlobalVariableSymbol globalVariable:
                    Append((Keyword, "global"), ' ');
                    AppendType(globalVariable.Type);
                    break;

                case ModuleSymbol:
                    Append((Keyword, "module"));
                    break;

                default:
                    Append((Literal, symbol.GetType().Name));
                    break;
            }

            NewLine();
        }

        private void WriteStatement(int indent, BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    WriteIndentedLine(indent, (Keyword, "Block"));
                    foreach (BoundStatement nested in block.Statements)
                        WriteStatement(indent + 1, nested);
                    break;

                case BoundVariableDeclarationStatement variableDecl:
                    Append(Space(indent * 2), (Keyword, "VarDecl"), ' ', (VariableName, variableDecl.Symbol, variableDecl.Symbol.Name), ':', ' ');
                    AppendType(variableDecl.Symbol.Type);
                    NewLine();
                    if (variableDecl.Initializer is not null)
                        WriteExpression(indent + 1, variableDecl.Initializer);
                    break;

                case BoundAssignmentStatement assignment:
                    WriteIndentedLine(indent, (Keyword, "Assign"), ' ', '(', (Literal, assignment.OperatorKind.ToString()), ')');
                    WriteAssignmentTarget(indent + 1, assignment.Target);
                    WriteExpression(indent + 1, assignment.Value);
                    break;

                case BoundMultiAssignmentStatement multiAssignment:
                    WriteIndentedLine(indent, (Keyword, "MultiAssign"));
                    foreach (BoundAssignmentTarget target in multiAssignment.Targets)
                        WriteAssignmentTarget(indent + 1, target);
                    WriteExpression(indent + 1, multiAssignment.Producer);
                    break;

                case BoundExpressionStatement expressionStatement:
                    WriteIndentedLine(indent, (Keyword, "ExprStmt"));
                    WriteExpression(indent + 1, expressionStatement.Expression);
                    break;

                case BoundIfStatement ifStatement:
                    WriteIndentedLine(indent, (Keyword, "If"));
                    WriteExpression(indent + 1, ifStatement.Condition);
                    WriteStatement(indent + 1, ifStatement.ThenBody);
                    if (ifStatement.ElseBody is not null)
                        WriteStatement(indent + 1, ifStatement.ElseBody);
                    break;

                case BoundWhileStatement whileStatement:
                    WriteIndentedLine(indent, (Keyword, "While"));
                    WriteExpression(indent + 1, whileStatement.Condition);
                    WriteStatement(indent + 1, whileStatement.Body);
                    break;

                case BoundForStatement forStatement:
                    WriteForStatement(indent, forStatement);
                    break;

                case BoundLoopStatement loopStatement:
                    WriteIndentedLine(indent, (Keyword, "Loop"));
                    WriteStatement(indent + 1, loopStatement.Body);
                    break;

                case BoundRepLoopStatement repLoop:
                    WriteIndentedLine(indent, (Keyword, "RepLoop"));
                    WriteStatement(indent + 1, repLoop.Body);
                    break;

                case BoundRepForStatement repFor:
                    Append(Space(indent * 2), (Keyword, "RepFor"));
                    if (repFor.Variable is not null)
                        Append(' ', '(', (VariableName, repFor.Variable, repFor.Variable.Name), ')');
                    NewLine();
                    WriteExpression(indent + 1, repFor.Start);
                    WriteExpression(indent + 1, repFor.End);
                    WriteStatement(indent + 1, repFor.Body);
                    break;

                case BoundNoirqStatement noirq:
                    WriteIndentedLine(indent, (Keyword, "Noirq"));
                    WriteStatement(indent + 1, noirq.Body);
                    break;

                case BoundReturnStatement ret:
                    WriteIndentedLine(indent, (Keyword, "Return"));
                    foreach (BoundExpression value in ret.Values)
                        WriteExpression(indent + 1, value);
                    break;

                case BoundBreakStatement:
                    WriteIndentedLine(indent, (Keyword, "Break"));
                    break;

                case BoundContinueStatement:
                    WriteIndentedLine(indent, (Keyword, "Continue"));
                    break;

                case BoundYieldStatement:
                    WriteIndentedLine(indent, (Keyword, "Yield"));
                    break;

                case BoundYieldtoStatement yieldto:
                    WriteIndentedLine(indent, (Keyword, "Yieldto"), ' ', '(', (FunctionName, yieldto.Target, yieldto.Target.Name), ')');
                    foreach (BoundExpression arg in yieldto.Arguments)
                        WriteExpression(indent + 1, arg);
                    break;

                case BoundAsmStatement asm:
                    WriteIndentedLine(indent, (Keyword, "Asm"), ' ', '[', (Literal, asm.Volatility.ToString()), ']', ' ', '(', (Literal, asm.FlagOutput?.ToString() ?? "no-flag"), ')');
                    break;

                case BoundErrorStatement:
                    WriteIndentedLine(indent, (Keyword, "ErrorStmt"));
                    break;

                default:
                    Assert.Unreachable($"Unhandled bound statement '{statement.GetType().Name}'."); // pragma: force-coverage
                    break; // pragma: force-coverage
            }
        }

        private void WriteForStatement(int indent, BoundForStatement forStatement)
        {
            Append(Space(indent * 2), (Keyword, "For"));
            if (forStatement.ItemVariable is not null)
            {
                Append(' ', '-', '>', ' ');
                if (forStatement.ItemIsMutable)
                    Append('&');
                Append((VariableName, forStatement.ItemVariable, forStatement.ItemVariable.Name));
                if (forStatement.IndexVariable?.Name.StartsWith("__", StringComparison.Ordinal) == false)
                    Append(',', ' ', (VariableName, forStatement.IndexVariable, forStatement.IndexVariable.Name));
            }
            else if (forStatement.IndexVariable?.Name.StartsWith("__", StringComparison.Ordinal) == false)
            {
                Append(' ', '-', '>', ' ', (VariableName, forStatement.IndexVariable, forStatement.IndexVariable.Name));
            }

            NewLine();
            WriteExpression(indent + 1, forStatement.Iterable);
            WriteStatement(indent + 1, forStatement.Body);
        }

        private void WriteExpression(int indent, BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLiteralExpression literal:
                    WriteTypedNodeHeader(indent, "Literal", literal.Type, (Literal, FormatLiteralValue(literal.Value)));
                    break;

                case BoundSymbolExpression symbol:
                    WriteTypedNodeHeader(indent, "Symbol", symbol.Type, (VariableName, symbol.Symbol, symbol.Symbol.Name));
                    break;

                case BoundUnaryExpression unary:
                    WriteTypedNodeHeader(indent, "Unary", unary.Type, (Literal, unary.Operator.Kind.ToString()));
                    WriteExpression(indent + 1, unary.Operand);
                    break;

                case BoundBinaryExpression binary:
                    WriteTypedNodeHeader(indent, "Binary", binary.Type, (Literal, binary.Operator.Kind.ToString()));
                    WriteExpression(indent + 1, binary.Left);
                    WriteExpression(indent + 1, binary.Right);
                    break;

                case BoundCallExpression call:
                    WriteTypedNodeHeader(indent, "Call", call.Type, (FunctionName, call.Function, call.Function.Name));
                    foreach (BoundExpression arg in call.Arguments)
                        WriteExpression(indent + 1, arg);
                    break;

                case BoundSpawnExpression spawn:
                    WriteTypedNodeHeader(indent, spawn.ResultSourceName, spawn.Type, (TaskName, spawn.Task, spawn.Task.Name), ' ', '[', (Literal, spawn.RequestedResultCount.ToString(CultureInfo.InvariantCulture)), ']');
                    foreach (BoundExpression arg in spawn.Arguments)
                        WriteExpression(indent + 1, arg);
                    break;

                case BoundIntrinsicCallExpression intrinsic:
                    WriteTypedNodeHeader(indent, "Intrinsic", intrinsic.Type, (Punctuation, "@"), (Literal, intrinsic.Mnemonic.ToString()));
                    foreach (BoundExpression arg in intrinsic.Arguments)
                        WriteExpression(indent + 1, arg);
                    break;

                case BoundEnumLiteralExpression enumLiteral:
                    WriteTypedNodeHeader(indent, "EnumLiteral", enumLiteral.Type, '.', (Literal, enumLiteral.MemberName), ' ', '=', ' ', (Literal, enumLiteral.Value.ToString(CultureInfo.InvariantCulture)));
                    break;

                case BoundArrayLiteralExpression arrayLiteral:
                    WriteTypedNodeHeader(indent, "ArrayLit", arrayLiteral.Type);
                    for (int i = 0; i < arrayLiteral.Elements.Count; i++)
                    {
                        if (i == arrayLiteral.Elements.Count - 1 && arrayLiteral.LastElementIsSpread)
                            WriteIndentedLine(indent + 1, '[', (Literal, i.ToString(CultureInfo.InvariantCulture)), ']', '.', '.', '.');
                        else
                            WriteIndentedLine(indent + 1, '[', (Literal, i.ToString(CultureInfo.InvariantCulture)), ']');
                        WriteExpression(indent + 2, arrayLiteral.Elements[i]);
                    }
                    break;

                case BoundMemberAccessExpression member:
                    WriteTypedNodeHeader(indent, "Member", member.Type, '.', (Literal, member.MemberName));
                    WriteExpression(indent + 1, member.Receiver);
                    break;

                case BoundIndexExpression index:
                    WriteTypedNodeHeader(indent, "Index", index.Type);
                    WriteExpression(indent + 1, index.Expression);
                    WriteExpression(indent + 1, index.Index);
                    break;

                case BoundPointerDerefExpression deref:
                    WriteTypedNodeHeader(indent, "Deref", deref.Type);
                    WriteExpression(indent + 1, deref.Expression);
                    break;

                case BoundIfExpression ifExpr:
                    WriteTypedNodeHeader(indent, "IfExpr", ifExpr.Type);
                    WriteExpression(indent + 1, ifExpr.Condition);
                    WriteExpression(indent + 1, ifExpr.ThenExpression);
                    WriteExpression(indent + 1, ifExpr.ElseExpression);
                    break;

                case BoundRangeExpression range:
                    WriteTypedNodeHeader(indent, "Range", range.Type, (Literal, range.IsInclusive ? "inclusive" : "exclusive"));
                    WriteExpression(indent + 1, range.Start);
                    WriteExpression(indent + 1, range.End);
                    break;

                case BoundStructLiteralExpression structLiteral:
                    WriteTypedNodeHeader(indent, "StructLit", structLiteral.Type);
                    foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                    {
                        WriteIndentedLine(indent + 1, (Literal, field.Name));
                        WriteExpression(indent + 2, field.Value);
                    }
                    break;

                case BoundConversionExpression conversion:
                    WriteTypedNodeHeader(indent, "Conversion", conversion.Type);
                    WriteExpression(indent + 1, conversion.Expression);
                    break;

                case BoundCastExpression cast:
                    WriteTypedNodeHeader(indent, "Cast", cast.Type);
                    WriteExpression(indent + 1, cast.Expression);
                    break;

                case BoundBitcastExpression bitcast:
                    WriteTypedNodeHeader(indent, "Bitcast", bitcast.Type);
                    WriteExpression(indent + 1, bitcast.Expression);
                    break;

                case BoundErrorExpression:
                    WriteIndentedLine(indent, (Keyword, "ErrorExpr"));
                    break;

                default:
                    Assert.Unreachable($"Unhandled bound expression '{expression.GetType().Name}'."); // pragma: force-coverage
                    break; // pragma: force-coverage
            }
        }

        private void WriteAssignmentTarget(int indent, BoundAssignmentTarget target)
        {
            switch (target)
            {
                case BoundSymbolAssignmentTarget symbol:
                    WriteTypedNodeHeader(indent, "TargetSymbol", symbol.Type, (VariableName, symbol.Symbol, symbol.Symbol.Name));
                    break;

                case BoundMemberAssignmentTarget member:
                    WriteTypedNodeHeader(indent, "TargetMember", member.Type, '.', (Literal, member.MemberName));
                    WriteExpression(indent + 1, member.Receiver);
                    break;

                case BoundBitfieldAssignmentTarget bitfield:
                    WriteTypedNodeHeader(indent, "TargetBitfield", bitfield.Type, '.', (Literal, bitfield.Member.Name));
                    WriteExpression(indent + 1, bitfield.ReceiverValue);
                    break;

                case BoundIndexAssignmentTarget index:
                    WriteTypedNodeHeader(indent, "TargetIndex", index.Type);
                    WriteExpression(indent + 1, index.Expression);
                    WriteExpression(indent + 1, index.Index);
                    break;

                case BoundPointerDerefAssignmentTarget deref:
                    WriteTypedNodeHeader(indent, "TargetDeref", deref.Type);
                    WriteExpression(indent + 1, deref.Expression);
                    break;

                case BoundDiscardAssignmentTarget discard:
                    WriteTypedNodeHeader(indent, "TargetDiscard", discard.Type);
                    break;

                case BoundErrorAssignmentTarget:
                    WriteIndentedLine(indent, (Keyword, "TargetError"));
                    break;

                default:
                    Assert.Unreachable($"Unhandled assignment target '{target.GetType().Name}'."); // pragma: force-coverage
                    break; // pragma: force-coverage
            }
        }

        private void WriteTypedNodeHeader(int indent, string name, BladeType type, params Span[] suffix)
        {
            Append(Space(indent * 2), (Keyword, name), '<');
            AppendType(type);
            Append('>');
            if (suffix.Length > 0)
            {
                Append(' ');
                Append(suffix);
            }

            NewLine();
        }

        private void WriteIndentedLine(int indent, params Span[] spans)
        {
            Append(Space(indent * 2));
            Append(spans);
            NewLine();
        }
    }

    private static string FormatLiteralValue(BladeValue value)
    {
        return value.Value switch
        {
            VoidValue => "void",
            UndefinedValue => "undefined",
            _ => value.Format(),
        };
    }
}
