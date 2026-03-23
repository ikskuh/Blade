using System.Linq;
using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class DiagnosticBagTests
{
    private static readonly TextSpan Span = new(1, 2);

    [Test]
    public void ReportMethods_EmitExpectedDiagnosticCodes()
    {
        DiagnosticBag bag = new();

        bag.Report(DiagnosticCode.E0001_UnexpectedCharacter, Span, "x");
        bag.ReportUnexpectedCharacter(Span, '$');
        bag.ReportUnterminatedString(Span);
        bag.ReportInvalidNumberLiteral(Span, "0x");
        bag.ReportUnterminatedBlockComment(Span);
        bag.ReportInvalidCharacterLiteral(Span);
        bag.ReportInvalidEscapeSequence(Span);
        bag.ReportUnexpectedToken(Span, "identifier", "EOF");
        bag.ReportExpectedExpression(Span);
        bag.ReportExpectedStatement(Span);
        bag.ReportExpectedTypeName(Span);
        bag.ReportExpectedIdentifier(Span);
        bag.ReportInvalidAssignmentTarget(Span);
        bag.ReportExpectedSemicolon(Span);
        bag.ReportSymbolAlreadyDeclared(Span, "a");
        bag.ReportUndefinedName(Span, "a");
        bag.ReportUndefinedType(Span, "T");
        bag.ReportCannotAssignToConstant(Span, "a");
        bag.ReportTypeMismatch(Span, "u32", "bool");
        bag.ReportNotCallable(Span, "u32");
        bag.ReportArgumentCountMismatch(Span, "f", 1, 2);
        bag.ReportInvalidLoopControl(Span, "break");
        bag.ReportInvalidBreakInRep(Span);
        bag.ReportInvalidYield(Span);
        bag.ReportInvalidYieldto(Span);
        bag.ReportReturnValueCountMismatch(Span, "f", 1, 0);
        bag.ReportReturnOutsideFunction(Span);
        bag.ReportInvalidYieldtoTarget(Span, "worker");
        bag.ReportInvalidLocalStorageClass(Span, "hub");
        bag.ReportInvalidExternScope(Span);
        bag.ReportInvalidParameterStorageClass(Span, "hub");
        bag.ReportUnsupportedStorageClass(Span, "hub");
        bag.ReportUnknownNamedArgument(Span, "f", "x");
        bag.ReportDuplicateNamedArgument(Span, "x");
        bag.ReportPositionalArgumentAfterNamed(Span, "f");
        bag.ReportNamedArgumentConflictsWithPositional(Span, "x");
        bag.ReportInvalidAddressOfTarget(Span);
        bag.ReportInvalidExplicitCast(Span, "u32", "bool");
        bag.ReportBitcastSizeMismatch(Span, "u32", "u16");
        bag.ReportAddressOfRecursiveLocal(Span, "x");
        bag.ReportMissingReturnValue(Span, "f");
        bag.ReportEnumLiteralRequiresContext(Span, "Idle");
        bag.ReportBitfieldWidthOverflow(Span, "Flags", "wide", 40, 32);
        bag.ReportArrayLiteralRequiresContext(Span);
        bag.ReportArrayLiteralSpreadMustBeLast(Span);
        bag.ReportInlineAsmUnknownInstruction(Span, "BLAH");
        bag.ReportInlineAsmUndefinedVariable(Span, "x");
        bag.ReportInlineAsmEmptyInstruction(Span);
        bag.ReportInlineAsmInvalidFlagOutput(Span, "Q");
        bag.ReportUnsupportedLowering(Span, "store.index");
        bag.ReportDuplicateVariableClause(Span, "@(...)");

        Assert.That(bag.Count, Is.EqualTo(51));
        Assert.That(bag.HasErrors, Is.True);
        Assert.That(bag.Last().Code, Is.EqualTo(DiagnosticCode.E0108_DuplicateVariableClause));
    }
}
