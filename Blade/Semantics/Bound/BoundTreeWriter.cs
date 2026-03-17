using System.Collections.Generic;
using System.Text;
using Blade;

namespace Blade.Semantics.Bound;

public static class BoundTreeWriter
{
    public static string Write(BoundProgram program)
    {
        Requires.NotNull(program);

        StringBuilder sb = new();
        AppendLine(sb, 0, "Program");

        AppendLine(sb, 1, "TypeAliases");
        foreach ((string aliasName, TypeSymbol aliasType) in program.TypeAliases)
            AppendLine(sb, 2, $"{aliasName}: {aliasType.Name}");

        AppendLine(sb, 1, "Globals");
        foreach (BoundGlobalVariableMember global in program.GlobalVariables)
        {
            AppendLine(sb, 2, $"{global.Symbol.Name}: {global.Symbol.Type.Name}");
            if (global.Initializer is not null)
                WriteExpression(sb, 3, global.Initializer);
        }

        AppendLine(sb, 1, "TopLevelStatements");
        foreach (BoundStatement statement in program.TopLevelStatements)
            WriteStatement(sb, 2, statement);

        AppendLine(sb, 1, "Functions");
        foreach (BoundFunctionMember function in program.Functions)
        {
            AppendLine(sb, 2, $"{function.Symbol.Name} ({function.Symbol.Kind})");
            WriteStatement(sb, 3, function.Body);
        }

        return sb.ToString();
    }

    private static void WriteStatement(StringBuilder sb, int indent, BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                AppendLine(sb, indent, "Block");
                foreach (BoundStatement nested in block.Statements)
                    WriteStatement(sb, indent + 1, nested);
                break;

            case BoundVariableDeclarationStatement variableDecl:
                AppendLine(sb, indent, $"VarDecl {variableDecl.Symbol.Name}: {variableDecl.Symbol.Type.Name}");
                if (variableDecl.Initializer is not null)
                    WriteExpression(sb, indent + 1, variableDecl.Initializer);
                break;

            case BoundAssignmentStatement assignment:
                AppendLine(sb, indent, $"Assign ({assignment.OperatorKind})");
                WriteAssignmentTarget(sb, indent + 1, assignment.Target);
                WriteExpression(sb, indent + 1, assignment.Value);
                break;

            case BoundExpressionStatement expressionStatement:
                AppendLine(sb, indent, "ExprStmt");
                WriteExpression(sb, indent + 1, expressionStatement.Expression);
                break;

            case BoundIfStatement ifStatement:
                AppendLine(sb, indent, "If");
                WriteExpression(sb, indent + 1, ifStatement.Condition);
                WriteStatement(sb, indent + 1, ifStatement.ThenBody);
                if (ifStatement.ElseBody is not null)
                    WriteStatement(sb, indent + 1, ifStatement.ElseBody);
                break;

            case BoundWhileStatement whileStatement:
                AppendLine(sb, indent, "While");
                WriteExpression(sb, indent + 1, whileStatement.Condition);
                WriteStatement(sb, indent + 1, whileStatement.Body);
                break;

            case BoundForStatement forStatement:
                AppendLine(sb, indent, $"For ({forStatement.Variable?.Name ?? "<error>"})");
                WriteStatement(sb, indent + 1, forStatement.Body);
                break;

            case BoundLoopStatement loopStatement:
                AppendLine(sb, indent, "Loop");
                WriteStatement(sb, indent + 1, loopStatement.Body);
                break;

            case BoundRepLoopStatement repLoop:
                AppendLine(sb, indent, "RepLoop");
                WriteExpression(sb, indent + 1, repLoop.Count);
                WriteStatement(sb, indent + 1, repLoop.Body);
                break;

            case BoundRepForStatement repFor:
                AppendLine(sb, indent, $"RepFor ({repFor.Variable.Name})");
                WriteExpression(sb, indent + 1, repFor.Start);
                WriteExpression(sb, indent + 1, repFor.End);
                WriteStatement(sb, indent + 1, repFor.Body);
                break;

            case BoundNoirqStatement noirq:
                AppendLine(sb, indent, "Noirq");
                WriteStatement(sb, indent + 1, noirq.Body);
                break;

            case BoundReturnStatement ret:
                AppendLine(sb, indent, "Return");
                foreach (BoundExpression value in ret.Values)
                    WriteExpression(sb, indent + 1, value);
                break;

            case BoundBreakStatement:
                AppendLine(sb, indent, "Break");
                break;

            case BoundContinueStatement:
                AppendLine(sb, indent, "Continue");
                break;

            case BoundYieldStatement:
                AppendLine(sb, indent, "Yield");
                break;

            case BoundYieldtoStatement yieldto:
                AppendLine(sb, indent, $"Yieldto ({yieldto.Target?.Name ?? "<error>"})");
                foreach (BoundExpression arg in yieldto.Arguments)
                    WriteExpression(sb, indent + 1, arg);
                break;

            case BoundAsmStatement asm:
                AppendLine(sb, indent, $"Asm [{asm.Volatility}] ({asm.FlagOutput ?? "no-flag"})");
                break;

            case BoundErrorStatement:
                AppendLine(sb, indent, "ErrorStmt");
                break;
        }
    }

    private static void WriteExpression(StringBuilder sb, int indent, BoundExpression expression)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal:
                AppendLine(sb, indent, $"Literal<{literal.Type.Name}> {literal.Value ?? "null"}");
                break;

            case BoundSymbolExpression symbol:
                AppendLine(sb, indent, $"Symbol<{symbol.Type.Name}> {symbol.Symbol.Name}");
                break;

            case BoundUnaryExpression unary:
                AppendLine(sb, indent, $"Unary<{unary.Type.Name}> {unary.Operator.Kind}");
                WriteExpression(sb, indent + 1, unary.Operand);
                break;

            case BoundBinaryExpression binary:
                AppendLine(sb, indent, $"Binary<{binary.Type.Name}> {binary.Operator.Kind}");
                WriteExpression(sb, indent + 1, binary.Left);
                WriteExpression(sb, indent + 1, binary.Right);
                break;

            case BoundCallExpression call:
                AppendLine(sb, indent, $"Call<{call.Type.Name}> {call.Function.Name}");
                foreach (BoundExpression arg in call.Arguments)
                    WriteExpression(sb, indent + 1, arg);
                break;

            case BoundIntrinsicCallExpression intrinsic:
                AppendLine(sb, indent, $"Intrinsic<{intrinsic.Type.Name}> @{intrinsic.Name}");
                foreach (BoundExpression arg in intrinsic.Arguments)
                    WriteExpression(sb, indent + 1, arg);
                break;

            case BoundMemberAccessExpression member:
                AppendLine(sb, indent, $"Member<{member.Type.Name}> .{member.MemberName}");
                WriteExpression(sb, indent + 1, member.Receiver);
                break;

            case BoundIndexExpression index:
                AppendLine(sb, indent, $"Index<{index.Type.Name}>");
                WriteExpression(sb, indent + 1, index.Expression);
                WriteExpression(sb, indent + 1, index.Index);
                break;

            case BoundPointerDerefExpression deref:
                AppendLine(sb, indent, $"Deref<{deref.Type.Name}>");
                WriteExpression(sb, indent + 1, deref.Expression);
                break;

            case BoundIfExpression ifExpr:
                AppendLine(sb, indent, $"IfExpr<{ifExpr.Type.Name}>");
                WriteExpression(sb, indent + 1, ifExpr.Condition);
                WriteExpression(sb, indent + 1, ifExpr.ThenExpression);
                WriteExpression(sb, indent + 1, ifExpr.ElseExpression);
                break;

            case BoundRangeExpression range:
                AppendLine(sb, indent, $"Range<{range.Type.Name}>");
                WriteExpression(sb, indent + 1, range.Start);
                WriteExpression(sb, indent + 1, range.End);
                break;

            case BoundStructLiteralExpression structLiteral:
                AppendLine(sb, indent, $"StructLit<{structLiteral.Type.Name}>");
                foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                {
                    AppendLine(sb, indent + 1, field.Name);
                    WriteExpression(sb, indent + 2, field.Value);
                }
                break;

            case BoundConversionExpression conversion:
                AppendLine(sb, indent, $"Conversion<{conversion.Type.Name}>");
                WriteExpression(sb, indent + 1, conversion.Expression);
                break;

            case BoundCastExpression cast:
                AppendLine(sb, indent, $"Cast<{cast.Type.Name}>");
                WriteExpression(sb, indent + 1, cast.Expression);
                break;

            case BoundBitcastExpression bitcast:
                AppendLine(sb, indent, $"Bitcast<{bitcast.Type.Name}>");
                WriteExpression(sb, indent + 1, bitcast.Expression);
                break;

            case BoundErrorExpression:
                AppendLine(sb, indent, "ErrorExpr");
                break;
        }
    }

    private static void WriteAssignmentTarget(StringBuilder sb, int indent, BoundAssignmentTarget target)
    {
        switch (target)
        {
            case BoundSymbolAssignmentTarget symbol:
                AppendLine(sb, indent, $"TargetSymbol<{symbol.Type.Name}> {symbol.Symbol.Name}");
                break;
            case BoundMemberAssignmentTarget member:
                AppendLine(sb, indent, $"TargetMember<{member.Type.Name}> .{member.MemberName}");
                WriteExpression(sb, indent + 1, member.Receiver);
                break;
            case BoundIndexAssignmentTarget index:
                AppendLine(sb, indent, $"TargetIndex<{index.Type.Name}>");
                WriteExpression(sb, indent + 1, index.Expression);
                WriteExpression(sb, indent + 1, index.Index);
                break;
            case BoundPointerDerefAssignmentTarget deref:
                AppendLine(sb, indent, $"TargetDeref<{deref.Type.Name}>");
                WriteExpression(sb, indent + 1, deref.Expression);
                break;
            case BoundErrorAssignmentTarget:
                AppendLine(sb, indent, "TargetError");
                break;
        }
    }

    private static void AppendLine(StringBuilder sb, int indent, string text)
    {
        sb.Append(' ', indent * 2);
        sb.AppendLine(text);
    }
}
