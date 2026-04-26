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

        bag.Report(new UnexpectedCharacterError(bag.CurrentSource, Span, '$'));
        bag.Report(new UnterminatedStringError(bag.CurrentSource, Span));
        bag.Report(new InvalidNumberLiteralError(bag.CurrentSource, Span, "0x"));
        bag.Report(new UnterminatedBlockCommentError(bag.CurrentSource, Span));
        bag.Report(new InvalidCharacterLiteralError(bag.CurrentSource, Span));
        bag.Report(new InvalidEscapeSequenceError(bag.CurrentSource, Span));
        bag.Report(new UnexpectedTokenError(bag.CurrentSource, Span, "identifier", "EOF"));
        bag.Report(new ExpectedExpressionError(bag.CurrentSource, Span));
        bag.Report(new ExpectedStatementError(bag.CurrentSource, Span));
        bag.Report(new ExpectedTypeNameError(bag.CurrentSource, Span));
        bag.Report(new ExpectedIdentifierError(bag.CurrentSource, Span));
        bag.Report(new InvalidAssignmentTargetError(bag.CurrentSource, Span));
        bag.Report(new ExpectedSemicolonError(bag.CurrentSource, Span));
        bag.Report(new SymbolAlreadyDeclaredError(bag.CurrentSource, Span, "a"));
        bag.Report(new UndefinedNameError(bag.CurrentSource, Span, "a"));
        bag.Report(new UndefinedTypeError(bag.CurrentSource, Span, "T"));
        bag.Report(new CannotAssignToConstantError(bag.CurrentSource, Span, "a"));
        bag.Report(new TypeMismatchError(bag.CurrentSource, Span, "u32", "bool"));
        bag.Report(new NotCallableError(bag.CurrentSource, Span, "u32"));
        bag.Report(new ArgumentCountMismatchError(bag.CurrentSource, Span, "f", 1, 2));
        bag.Report(new InvalidLoopControlError(bag.CurrentSource, Span, "break"));
        bag.Report(new InvalidBreakInRepLoopError(bag.CurrentSource, Span));
        bag.Report(new InvalidYieldUsageError(bag.CurrentSource, Span));
        bag.Report(new InvalidYieldtoUsageError(bag.CurrentSource, Span));
        bag.Report(new ReturnValueCountMismatchError(bag.CurrentSource, Span, "f", 1, 0));
        bag.Report(new ReturnOutsideFunctionError(bag.CurrentSource, Span));
        bag.Report(new InvalidYieldtoTargetError(bag.CurrentSource, Span, "worker"));
        bag.Report(new InvalidLocalStorageClassError(bag.CurrentSource, Span, "hub"));
        bag.Report(new InvalidExternScopeError(bag.CurrentSource, Span));
        bag.Report(new InvalidParameterStorageClassError(bag.CurrentSource, Span, "hub"));
        bag.Report(new UnknownNamedArgumentError(bag.CurrentSource, Span, "f", "x"));
        bag.Report(new DuplicateNamedArgumentError(bag.CurrentSource, Span, "x"));
        bag.Report(new PositionalArgumentAfterNamedError(bag.CurrentSource, Span, "f"));
        bag.Report(new NamedArgumentConflictsWithPositionalError(bag.CurrentSource, Span, "x"));
        bag.Report(new InvalidAddressOfTargetError(bag.CurrentSource, Span));
        bag.Report(new InvalidExplicitCastError(bag.CurrentSource, Span, "u32", "bool"));
        bag.Report(new BitcastSizeMismatchError(bag.CurrentSource, Span, "u32", "u16"));
        bag.Report(new AddressOfRecursiveLocalError(bag.CurrentSource, Span, "x"));
        bag.Report(new MissingReturnValueError(bag.CurrentSource, Span, "f"));
        bag.Report(new ReturnFromCoroutineError(bag.CurrentSource, Span, "worker"));
        bag.Report(new ExpressionNotAStatementError(bag.CurrentSource, Span));
        bag.Report(new RangeIterationRequiresBindingError(bag.CurrentSource, Span));
        bag.Report(new EnumLiteralRequiresContextError(bag.CurrentSource, Span, "Idle"));
        bag.Report(new BitfieldWidthOverflowError(bag.CurrentSource, Span, "Flags", "wide", 40, 32));
        bag.Report(new ArrayLiteralRequiresContextError(bag.CurrentSource, Span));
        bag.Report(new ArrayLiteralSpreadMustBeLastError(bag.CurrentSource, Span));
        bag.Report(new AccessToForeignLayoutError(bag.CurrentSource, Span, "State", "value"));
        bag.Report(new UnsupportedGlobalStorageError(bag.CurrentSource, Span, "cog"));
        bag.Report(new InlineAsmUnknownInstructionError(bag.CurrentSource, Span, "BLAH"));
        bag.Report(new InlineAsmUndefinedVariableError(bag.CurrentSource, Span, "x"));
        bag.Report(new InlineAsmEmptyInstructionError(bag.CurrentSource, Span));
        bag.Report(new InlineAsmInvalidFlagOutputError(bag.CurrentSource, Span, "Q"));
        bag.Report(new InlineAsmTempReadBeforeWriteWarning(bag.CurrentSource, Span, "%0"));
        bag.Report(new ComptimeIntegerTruncationWarning(bag.CurrentSource, Span, "257", "u8", "1"));
        bag.Report(new UnsupportedLoweringError(bag.CurrentSource, Span, "store.index"));
        bag.Report(new DuplicateVariableClauseError(bag.CurrentSource, Span, "@(...)"));

        Assert.That(bag.Count, Is.EqualTo(56));
        Assert.That(bag.HasErrors, Is.True);
        Assert.That(bag.Last().Code, Is.EqualTo("E0108"));
    }

    [Test]
    public void WarningOnlyBag_DoesNotReportErrors()
    {
        DiagnosticBag bag = new();
        using IDisposable _ = bag.UseSource(new SourceText(string.Empty, "<input>"));

        bag.Report(new InlineAsmTempReadBeforeWriteWarning(bag.CurrentSource, Span, "%0"));

        Assert.That(bag.Count, Is.EqualTo(1));
        Assert.That(bag.ErrorCount, Is.EqualTo(0));
        Assert.That(bag.HasErrors, Is.False);
        Assert.That(bag.Single().Code, Is.EqualTo("W0307"));
    }
}
