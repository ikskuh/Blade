using System.Linq;
using Blade.Diagnostics;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class DiagnosticBagTests
{
    private static readonly TextSpan Span = new(1, 2);

    [Test]
    public void ReportMethods_EmitExpectedCodes()
    {
        DiagnosticBag bag = new();
        using IDisposable _ = bag.UseSource(new SourceText(string.Empty, "<input>"));

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
        bag.ReportUnknownNamedArgument(Span, "f", "x");
        bag.ReportDuplicateNamedArgument(Span, "x");
        bag.ReportPositionalArgumentAfterNamed(Span, "f");
        bag.ReportNamedArgumentConflictsWithPositional(Span, "x");
        bag.ReportInvalidAddressOfTarget(Span);
        bag.ReportInvalidExplicitCast(Span, "u32", "bool");
        bag.ReportBitcastSizeMismatch(Span, "u32", "u16");
        bag.ReportAddressOfRecursiveLocal(Span, "x");
        bag.ReportMissingReturnValue(Span, "f");
        bag.ReportReturnFromCoroutine(Span, "worker");
        bag.ReportExpressionNotAStatement(Span);
        bag.ReportRangeIterationRequiresBinding(Span);
        bag.ReportEnumLiteralRequiresContext(Span, "Idle");
        bag.ReportBitfieldWidthOverflow(Span, "Flags", "wide", 40, 32);
        bag.ReportArrayLiteralRequiresContext(Span);
        bag.ReportArrayLiteralSpreadMustBeLast(Span);
        bag.ReportAccessToForeignLayout(Span, "State", "value");
        bag.ReportUnsupportedGlobalStorage(Span, "cog");
        bag.ReportInlineAsmUnknownInstruction(Span, "BLAH");
        bag.ReportInlineAsmUndefinedVariable(Span, "x");
        bag.ReportInlineAsmEmptyInstruction(Span);
        bag.ReportInlineAsmInvalidFlagOutput(Span, "Q");
        bag.ReportInlineAsmTempReadBeforeWrite(Span, "%0");
        bag.ReportComptimeIntegerTruncation(Span, "257", "u8", "1");
        bag.ReportUnsupportedLowering(Span, "store.index");
        bag.ReportDuplicateVariableClause(Span, "@(...)");

        Assert.That(bag.Count, Is.EqualTo(56));
        Assert.That(bag.HasErrors, Is.True);
        Assert.That(bag.Last().Code, Is.EqualTo("E0108"));
    }

    [Test]
    public void WarningOnlyBag_DoesNotReportErrors()
    {
        DiagnosticBag bag = new();
        using IDisposable _ = bag.UseSource(new SourceText(string.Empty, "<input>"));

        bag.ReportInlineAsmTempReadBeforeWrite(Span, "%0");

        Assert.That(bag.Count, Is.EqualTo(1));
        Assert.That(bag.ErrorCount, Is.EqualTo(0));
        Assert.That(bag.HasErrors, Is.False);
        Assert.That(bag.Single().Code, Is.EqualTo("W0307"));
    }
}
