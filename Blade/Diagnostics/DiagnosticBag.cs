using System;
using System.Collections;
using System.Collections.Generic;
using Blade;
using Blade.Source;

namespace Blade.Diagnostics;

/// <summary>
/// Collects diagnostics during compilation.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = new();
    private SourceText? _currentSource;

    public int Count => _diagnostics.Count;

    public int ErrorCount
    {
        get
        {
            int count = 0;
            foreach (Diagnostic diagnostic in _diagnostics)
            {
                if (diagnostic.IsError)
                    count++;
            }

            return count;
        }
    }

    public bool HasErrors => ErrorCount > 0;

    public IDisposable UseSource(SourceText source)
    {
        Requires.NotNull(source);
        SourceText? previous = _currentSource;
        _currentSource = source;
        return new SourceScope(this, previous);
    }

    /// <summary>
    /// Adds a typed diagnostic message to the bag.
    /// </summary>
    public void Report(DiagnosticMessage message)
    {
        _diagnostics.Add(new Diagnostic(Requires.NotNull(message)));
    }

    private SourceText CurrentSource
    {
        get
        {
            Assert.Invariant(_currentSource is not null, "Diagnostics require a current source context. Use DiagnosticBag.UseSource(source) when reporting.");
            return _currentSource;
        }
    }

    public void ReportUnexpectedCharacter(TextSpan span, char character)
    {
        Report(new UnexpectedCharacterError(CurrentSource, span, character));
    }

    public void ReportUnterminatedString(TextSpan span)
    {
        Report(new UnterminatedStringError(CurrentSource, span));
    }

    public void ReportInvalidNumberLiteral(TextSpan span, string text)
    {
        Report(new InvalidNumberLiteralError(CurrentSource, span, text));
    }

    public void ReportUnterminatedBlockComment(TextSpan span)
    {
        Report(new UnterminatedBlockCommentError(CurrentSource, span));
    }

    public void ReportInvalidCharacterLiteral(TextSpan span)
    {
        Report(new InvalidCharacterLiteralError(CurrentSource, span));
    }

    public void ReportInvalidEscapeSequence(TextSpan span)
    {
        Report(new InvalidEscapeSequenceError(CurrentSource, span));
    }

    public void ReportInvalidUtf8(TextSpan span)
    {
        Report(new InvalidUtf8Error(CurrentSource, span));
    }

    public void ReportInvalidControlCharacter(TextSpan span, char character)
    {
        Report(new InvalidControlCharacterError(CurrentSource, span, character));
    }

    public void ReportUnexpectedToken(TextSpan span, string expected, string actual)
    {
        Report(new UnexpectedTokenError(CurrentSource, span, expected, actual));
    }

    public void ReportExpectedExpression(TextSpan span)
    {
        Report(new ExpectedExpressionError(CurrentSource, span));
    }

    public void ReportExpectedStatement(TextSpan span)
    {
        Report(new ExpectedStatementError(CurrentSource, span));
    }

    public void ReportExpectedTypeName(TextSpan span)
    {
        Report(new ExpectedTypeNameError(CurrentSource, span));
    }

    public void ReportExpectedIdentifier(TextSpan span)
    {
        Report(new ExpectedIdentifierError(CurrentSource, span));
    }

    public void ReportInvalidAssignmentTarget(TextSpan span)
    {
        Report(new InvalidAssignmentTargetError(CurrentSource, span));
    }

    public void ReportExpectedSemicolon(TextSpan span)
    {
        Report(new ExpectedSemicolonError(CurrentSource, span));
    }

    public void ReportDuplicateVariableClause(TextSpan span, string clauseName)
    {
        Report(new DuplicateVariableClauseError(CurrentSource, span, clauseName));
    }

    public void ReportSymbolAlreadyDeclared(TextSpan span, string name)
    {
        Report(new SymbolAlreadyDeclaredError(CurrentSource, span, name));
    }

    public void ReportUndefinedName(TextSpan span, string name)
    {
        Report(new UndefinedNameError(CurrentSource, span, name));
    }

    public void ReportUndefinedType(TextSpan span, string name)
    {
        Report(new UndefinedTypeError(CurrentSource, span, name));
    }

    public void ReportCannotAssignToConstant(TextSpan span, string name)
    {
        Report(new CannotAssignToConstantError(CurrentSource, span, name));
    }

    public void ReportTypeMismatch(TextSpan span, string expected, string actual)
    {
        Report(new TypeMismatchError(CurrentSource, span, expected, actual));
    }

    public void ReportNotCallable(TextSpan span, string typeName)
    {
        Report(new NotCallableError(CurrentSource, span, typeName));
    }

    public void ReportArgumentCountMismatch(TextSpan span, string functionName, int expected, int actual)
    {
        Report(new ArgumentCountMismatchError(CurrentSource, span, functionName, expected, actual));
    }

    public void ReportInvalidLoopControl(TextSpan span, string keyword)
    {
        Report(new InvalidLoopControlError(CurrentSource, span, keyword));
    }

    public void ReportInvalidBreakInRep(TextSpan span)
    {
        Report(new InvalidBreakInRepLoopError(CurrentSource, span));
    }

    public void ReportInvalidYield(TextSpan span)
    {
        Report(new InvalidYieldUsageError(CurrentSource, span));
    }

    public void ReportInvalidYieldto(TextSpan span)
    {
        Report(new InvalidYieldtoUsageError(CurrentSource, span));
    }

    public void ReportReturnValueCountMismatch(TextSpan span, string functionName, int expected, int actual)
    {
        Report(new ReturnValueCountMismatchError(CurrentSource, span, functionName, expected, actual));
    }

    public void ReportReturnOutsideFunction(TextSpan span)
    {
        Report(new ReturnOutsideFunctionError(CurrentSource, span));
    }

    public void ReportInvalidYieldtoTarget(TextSpan span, string target)
    {
        Report(new InvalidYieldtoTargetError(CurrentSource, span, target));
    }

    public void ReportInvalidLocalStorageClass(TextSpan span, string storageClass)
    {
        Report(new InvalidLocalStorageClassError(CurrentSource, span, storageClass));
    }

    public void ReportInvalidExternScope(TextSpan span)
    {
        Report(new InvalidExternScopeError(CurrentSource, span));
    }

    public void ReportInvalidParameterStorageClass(TextSpan span, string storageClass)
    {
        Report(new InvalidParameterStorageClassError(CurrentSource, span, storageClass));
    }

    public void ReportPointerStorageClassRequired(TextSpan span)
    {
        Report(new PointerStorageClassRequiredError(CurrentSource, span));
    }

    public void ReportUnknownNamedArgument(TextSpan span, string functionName, string parameterName)
    {
        Report(new UnknownNamedArgumentError(CurrentSource, span, functionName, parameterName));
    }

    public void ReportDuplicateNamedArgument(TextSpan span, string parameterName)
    {
        Report(new DuplicateNamedArgumentError(CurrentSource, span, parameterName));
    }

    public void ReportPositionalArgumentAfterNamed(TextSpan span, string functionName)
    {
        Report(new PositionalArgumentAfterNamedError(CurrentSource, span, functionName));
    }

    public void ReportNamedArgumentConflictsWithPositional(TextSpan span, string parameterName)
    {
        Report(new NamedArgumentConflictsWithPositionalError(CurrentSource, span, parameterName));
    }

    public void ReportInvalidAddressOfTarget(TextSpan span)
    {
        Report(new InvalidAddressOfTargetError(CurrentSource, span));
    }

    public void ReportInvalidExplicitCast(TextSpan span, string sourceType, string targetType)
    {
        Report(new InvalidExplicitCastError(CurrentSource, span, sourceType, targetType));
    }

    public void ReportBitcastSizeMismatch(TextSpan span, string sourceType, string targetType)
    {
        Report(new BitcastSizeMismatchError(CurrentSource, span, sourceType, targetType));
    }

    public void ReportAddressOfRecursiveLocal(TextSpan span, string name)
    {
        Report(new AddressOfRecursiveLocalError(CurrentSource, span, name));
    }

    public void ReportMissingReturnValue(TextSpan span, string functionName)
    {
        Report(new MissingReturnValueError(CurrentSource, span, functionName));
    }

    public void ReportReturnFromCoroutine(TextSpan span, string functionName)
    {
        Report(new ReturnFromCoroutineError(CurrentSource, span, functionName));
    }

    public void ReportFileImportAliasRequired(TextSpan span)
    {
        Report(new FileImportAliasRequiredError(CurrentSource, span));
    }

    public void ReportUnknownNamedModule(TextSpan span, string moduleName)
    {
        Report(new UnknownNamedModuleError(CurrentSource, span, moduleName));
    }

    public void ReportImportFileNotFound(TextSpan span, string path)
    {
        Report(new ImportFileNotFoundError(CurrentSource, span, path));
    }

    public void ReportCircularImport(TextSpan span, string path)
    {
        Report(new CircularImportError(CurrentSource, span, path));
    }

    public void ReportEnumLiteralRequiresContext(TextSpan span, string memberName)
    {
        Report(new EnumLiteralRequiresContextError(CurrentSource, span, memberName));
    }

    public void ReportBitfieldWidthOverflow(TextSpan span, string bitfieldName, string fieldName, int usedBits, int backingBits)
    {
        Report(new BitfieldWidthOverflowError(CurrentSource, span, bitfieldName, fieldName, usedBits, backingBits));
    }

    public void ReportArrayLiteralRequiresContext(TextSpan span)
    {
        Report(new ArrayLiteralRequiresContextError(CurrentSource, span));
    }

    public void ReportArrayLiteralSpreadMustBeLast(TextSpan span)
    {
        Report(new ArrayLiteralSpreadMustBeLastError(CurrentSource, span));
    }

    public void ReportStructUnknownField(TextSpan span, string structName, string fieldName)
    {
        Report(new StructUnknownFieldError(CurrentSource, span, structName, fieldName));
    }

    public void ReportStructMissingFields(TextSpan span, string structName, string missingFields)
    {
        Report(new StructMissingFieldsError(CurrentSource, span, structName, missingFields));
    }

    public void ReportStructDuplicateField(TextSpan span, string fieldName)
    {
        Report(new StructDuplicateFieldError(CurrentSource, span, fieldName));
    }

    public void ReportStringLengthMismatch(TextSpan span, int expectedLength, int actualLength)
    {
        Report(new StringLengthMismatchError(CurrentSource, span, expectedLength, actualLength));
    }

    public void ReportStringToNonConstPointer(TextSpan span)
    {
        Report(new StringToNonConstPointerError(CurrentSource, span));
    }

    public void ReportComptimeValueRequired(TextSpan span)
    {
        Report(new ComptimeValueRequiredError(CurrentSource, span));
    }

    public void ReportComptimeUnsupportedConstruct(TextSpan span, string detail)
    {
        Report(new ComptimeUnsupportedConstructError(CurrentSource, span, detail));
    }

    public void ReportComptimeForbiddenSymbolAccess(TextSpan span, string detail)
    {
        Report(new ComptimeForbiddenSymbolAccessError(CurrentSource, span, detail));
    }

    public void ReportComptimeFuelExhausted(TextSpan span)
    {
        Report(new ComptimeFuelExhaustedError(CurrentSource, span));
    }

    public void ReportDuplicateNamedModuleRoot(TextSpan span, string firstModuleName, string secondModuleName, string path)
    {
        Report(new DuplicateNamedModuleRootError(CurrentSource, span, firstModuleName, secondModuleName, path));
    }

    public void ReportMultiAssignmentRequiresCall(TextSpan span)
    {
        Report(new MultiAssignmentRequiresCallError(CurrentSource, span));
    }

    public void ReportMultiAssignmentTargetCountMismatch(TextSpan span, string expressionName, int expected, int actual)
    {
        Report(new MultiAssignmentTargetCountMismatchError(CurrentSource, span, expressionName, expected, actual));
    }

    public void ReportDiscardInExpression(TextSpan span)
    {
        Report(new DiscardInExpressionError(CurrentSource, span));
    }

    public void ReportExternCannotHaveInitializer(TextSpan span, string name)
    {
        Report(new ExternCannotHaveInitializerError(CurrentSource, span, name));
    }

    public void ReportMemoryofRequiresVariable(TextSpan span)
    {
        Report(new MemoryofRequiresVariableError(CurrentSource, span));
    }

    public void ReportQueryRequiresMemorySpace(TextSpan span, string operatorName)
    {
        Report(new QueryRequiresMemorySpaceError(CurrentSource, span, operatorName));
    }

    public void ReportQueryUnsupportedType(TextSpan span, string operatorName, string typeName)
    {
        Report(new QueryUnsupportedTypeError(CurrentSource, span, operatorName, typeName));
    }

    public void ReportQueryAutomaticLocal(TextSpan span, string operatorName, string variableName)
    {
        Report(new QueryAutomaticLocalError(CurrentSource, span, operatorName, variableName));
    }

    public void ReportInvalidMemorySpaceArgument(TextSpan span)
    {
        Report(new InvalidMemorySpaceArgumentError(CurrentSource, span));
    }

    public void ReportAssertionFailed(TextSpan span, string? message)
    {
        string assertionMessage = message is null ? "assertion failed" : $"assertion failed: {message}";
        Report(new AssertionFailedError(CurrentSource, span, assertionMessage));
    }

    public void ReportUnknownBuiltin(TextSpan span, string name)
    {
        Report(new UnknownBuiltinError(CurrentSource, span, name));
    }

    public void ReportInvalidPointerArithmetic(TextSpan span, string operation)
    {
        Report(new InvalidPointerArithmeticError(CurrentSource, span, operation));
    }

    public void ReportIncompatiblePointerSubtraction(TextSpan span, string leftType, string rightType)
    {
        Report(new IncompatiblePointerSubtractionError(CurrentSource, span, leftType, rightType));
    }

    public void ReportExpressionNotAStatement(TextSpan span)
    {
        Report(new ExpressionNotAStatementError(CurrentSource, span));
    }

    public void ReportRangeIterationRequiresBinding(TextSpan span)
    {
        Report(new RangeIterationRequiresBindingError(CurrentSource, span));
    }

    public void ReportRangeExpressionOutsideForLoop(TextSpan span)
    {
        Report(new RangeExpressionOutsideForLoopError(CurrentSource, span));
    }

    public void ReportComptimeIntegerTruncation(TextSpan span, string value, string targetType, string truncatedValue)
    {
        Report(new ComptimeIntegerTruncationWarning(CurrentSource, span, value, targetType, truncatedValue));
    }

    public void ReportAddressOfParameter(TextSpan span, string name)
    {
        Report(new AddressOfParameterError(CurrentSource, span, name));
    }

    public void ReportAmbiguousLayoutMemberAccess(TextSpan span, string name, IReadOnlyList<string> layoutNames)
    {
        string layoutNamesText = string.Join(", ", layoutNames);
        Report(new AmbiguousLayoutMemberAccessError(CurrentSource, span, name, layoutNamesText));
    }

    public void ReportLexicalNameConflictsWithLayoutMember(TextSpan span, string name, IReadOnlyList<string> layoutNames)
    {
        string layoutNamesText = string.Join(", ", layoutNames);
        Report(new LexicalNameConflictsWithLayoutMemberWarning(CurrentSource, span, name, layoutNamesText));
    }

    public void ReportLayoutMemberShadowsParentMember(TextSpan span, string layoutName, string memberName)
    {
        Report(new LayoutMemberShadowsParentMemberWarning(CurrentSource, span, layoutName, memberName));
    }

    public void ReportTaskLayoutCannotBeInherited(TextSpan span, string taskName)
    {
        Report(new TaskLayoutCannotBeInheritedError(CurrentSource, span, taskName));
    }

    public void ReportInvalidSpawnTarget(TextSpan span, string targetName)
    {
        Report(new InvalidSpawnTargetError(CurrentSource, span, targetName));
    }

    public void ReportMainTaskMustBeCog(TextSpan span, string taskName, Blade.Semantics.VariableStorageClass storageClass)
    {
        string storageClassKeyword = storageClass switch
        {
            Blade.Semantics.VariableStorageClass.Cog => "cog",
            Blade.Semantics.VariableStorageClass.Lut => "lut",
            Blade.Semantics.VariableStorageClass.Hub => "hub",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };

        Report(new MainTaskMustBeCogWarning(CurrentSource, span, taskName, storageClassKeyword));
    }

    public void ReportMissingMainTask(TextSpan span)
    {
        Report(new MissingMainTaskError(CurrentSource, span));
    }

    public void ReportFunctionLayoutSubsetViolation(TextSpan span, string callerName, string calleeName, IReadOnlyList<string> callerLayouts, IReadOnlyList<string> calleeLayouts)
    {
        Requires.NotNull(callerLayouts);
        Requires.NotNull(calleeLayouts);

        string callerLayoutText = callerLayouts.Count == 0 ? "<none>" : string.Join(", ", callerLayouts);
        string calleeLayoutText = calleeLayouts.Count == 0 ? "<none>" : string.Join(", ", calleeLayouts);
        Report(new FunctionLayoutSubsetViolationError(CurrentSource, span, callerName, calleeName, callerLayoutText, calleeLayoutText));
    }

    public void ReportDuplicateFunctionLayoutMetadata(TextSpan span)
    {
        Report(new DuplicateFunctionLayoutMetadataWarning(CurrentSource, span));
    }

    public void ReportDuplicateFunctionAlignMetadata(TextSpan span)
    {
        Report(new DuplicateFunctionAlignMetadataError(CurrentSource, span));
    }

    public void ReportInvalidFunctionAlignment(TextSpan span, int alignment)
    {
        Report(new InvalidFunctionAlignmentError(CurrentSource, span, alignment));
    }

    public void ReportTaskLayoutNotAllowedInFunctionMetadata(TextSpan span, string taskName)
    {
        Report(new TaskLayoutNotAllowedInFunctionMetadataError(CurrentSource, span, taskName));
    }

    public void ReportAccessToForeignLayout(TextSpan span, string layoutName, string memberName)
    {
        Report(new AccessToForeignLayoutError(CurrentSource, span, layoutName, memberName));
    }

    public void ReportUnsupportedGlobalStorage(TextSpan span, string storageClass)
    {
        Report(new UnsupportedGlobalStorageError(CurrentSource, span, storageClass));
    }

    public void ReportInvalidLayoutAlignment(TextSpan span, string layoutName, string memberName, int alignment)
    {
        Report(new InvalidLayoutAlignmentError(CurrentSource, span, layoutName, memberName, alignment));
    }

    public void ReportInvalidLayoutAddress(TextSpan span, string layoutName, string memberName, Blade.Semantics.VariableStorageClass storageClass, int address, int sizeInAddressUnits)
    {
        string storageName = storageClass switch
        {
            Blade.Semantics.VariableStorageClass.Cog => "cog",
            Blade.Semantics.VariableStorageClass.Lut => "lut",
            Blade.Semantics.VariableStorageClass.Hub => "hub",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };

        Report(new InvalidLayoutAddressError(CurrentSource, span, layoutName, memberName, storageName, address, sizeInAddressUnits));
    }

    public void ReportLayoutAddressConflict(TextSpan span, string layoutName, string memberName, Blade.Semantics.VariableStorageClass storageClass, int address, string conflictingLayoutName, string conflictingMemberName, int conflictingAddress)
    {
        string storageName = storageClass switch
        {
            Blade.Semantics.VariableStorageClass.Cog => "cog",
            Blade.Semantics.VariableStorageClass.Lut => "lut",
            Blade.Semantics.VariableStorageClass.Hub => "hub",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };

        Report(new LayoutAddressConflictError(CurrentSource, span, layoutName, memberName, storageName, address, conflictingLayoutName, conflictingMemberName, conflictingAddress));
    }

    public void ReportLayoutAllocationFailed(TextSpan span, string layoutName, string memberName, Blade.Semantics.VariableStorageClass storageClass, int sizeInAddressUnits, int alignmentInAddressUnits)
    {
        string storageName = storageClass switch
        {
            Blade.Semantics.VariableStorageClass.Cog => "cog",
            Blade.Semantics.VariableStorageClass.Lut => "lut",
            Blade.Semantics.VariableStorageClass.Hub => "hub",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };

        Report(new LayoutAllocationFailedError(CurrentSource, span, layoutName, memberName, storageName, sizeInAddressUnits, alignmentInAddressUnits));
    }

    public void ReportInlineAsmUnknownInstruction(TextSpan span, string mnemonic)
    {
        Report(new InlineAsmUnknownInstructionError(CurrentSource, span, mnemonic));
    }

    public void ReportInlineAsmUndefinedVariable(TextSpan span, string name)
    {
        Report(new InlineAsmUndefinedVariableError(CurrentSource, span, name));
    }

    public void ReportInlineAsmEmptyInstruction(TextSpan span)
    {
        Report(new InlineAsmEmptyInstructionError(CurrentSource, span));
    }

    public void ReportInlineAsmInvalidFlagOutput(TextSpan span, string flag)
    {
        Report(new InlineAsmInvalidFlagOutputError(CurrentSource, span, flag));
    }

    public void ReportInlineAsmInvalidInstructionForm(TextSpan span, string mnemonic, int operandCount)
    {
        Report(new InlineAsmInvalidInstructionFormError(CurrentSource, span, mnemonic, operandCount));
    }

    public void ReportInlineAsmUndefinedLabel(TextSpan span, string name)
    {
        Report(new InlineAsmUndefinedLabelError(CurrentSource, span, name));
    }

    public void ReportInlineAsmTempReadBeforeWrite(TextSpan span, string name)
    {
        Report(new InlineAsmTempReadBeforeWriteWarning(CurrentSource, span, name));
    }

    public void ReportUnsupportedLowering(TextSpan span, string lowering)
    {
        Report(new UnsupportedLoweringError(CurrentSource, span, lowering));
    }

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private readonly struct SourceScope(DiagnosticBag bag, SourceText? previous) : IDisposable
    {
        private readonly DiagnosticBag _bag = bag;
        private readonly SourceText? _previous = previous;

        public void Dispose()
        {
            _bag._currentSource = _previous;
        }
    }
}
