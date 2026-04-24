using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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

    public void Report(DiagnosticCode code, TextSpan span, string message)
    {
        Assert.Invariant(_currentSource is not null, "Diagnostics require a current source context. Use DiagnosticBag.UseSource(source) when reporting.");
        _diagnostics.Add(new Diagnostic(_currentSource, code, span, message));
    }

    public void ReportUnexpectedCharacter(TextSpan span, char character)
    {
        Report(DiagnosticCode.E0001_UnexpectedCharacter, span, $"Unexpected character '{character}'.");
    }

    public void ReportUnterminatedString(TextSpan span)
    {
        Report(DiagnosticCode.E0002_UnterminatedString, span, "Unterminated string literal.");
    }

    public void ReportInvalidNumberLiteral(TextSpan span, string text)
    {
        Report(DiagnosticCode.E0003_InvalidNumberLiteral, span, $"Invalid number literal '{text}'.");
    }

    public void ReportUnterminatedBlockComment(TextSpan span)
    {
        Report(DiagnosticCode.E0004_UnterminatedBlockComment, span, "Unterminated block comment.");
    }

    public void ReportInvalidCharacterLiteral(TextSpan span)
    {
        Report(DiagnosticCode.E0005_InvalidCharacterLiteral, span, "Invalid character literal.");
    }

    public void ReportInvalidEscapeSequence(TextSpan span)
    {
        Report(DiagnosticCode.E0006_InvalidEscapeSequence, span, "Invalid escape sequence.");
    }

    public void ReportInvalidUtf8(TextSpan span)
    {
        Report(DiagnosticCode.E0007_InvalidUtf8, span, "Source file is not valid UTF-8.");
    }

    public void ReportInvalidControlCharacter(TextSpan span, char character)
    {
        Report(
            DiagnosticCode.E0008_InvalidControlCharacter,
            span,
            string.Format(CultureInfo.InvariantCulture, "Control character U+{0:X4} is not allowed in Blade source files.", (int)character));
    }

    // Parser diagnostics

    public void ReportUnexpectedToken(TextSpan span, string expected, string actual)
    {
        Report(DiagnosticCode.E0101_UnexpectedToken, span, $"Expected {expected}, got '{actual}'.");
    }

    public void ReportExpectedExpression(TextSpan span)
    {
        Report(DiagnosticCode.E0102_ExpectedExpression, span, "Expected expression.");
    }

    public void ReportExpectedStatement(TextSpan span)
    {
        Report(DiagnosticCode.E0103_ExpectedStatement, span, "Expected statement.");
    }

    public void ReportExpectedTypeName(TextSpan span)
    {
        Report(DiagnosticCode.E0104_ExpectedTypeName, span, "Expected type name.");
    }

    public void ReportExpectedIdentifier(TextSpan span)
    {
        Report(DiagnosticCode.E0105_ExpectedIdentifier, span, "Expected identifier.");
    }

    public void ReportInvalidAssignmentTarget(TextSpan span)
    {
        Report(DiagnosticCode.E0106_InvalidAssignmentTarget, span, "Invalid assignment target.");
    }

    public void ReportExpectedSemicolon(TextSpan span)
    {
        Report(DiagnosticCode.E0107_ExpectedSemicolon, span, "Expected ';'.");
    }

    public void ReportDuplicateVariableClause(TextSpan span, string clauseName)
    {
        Report(DiagnosticCode.E0108_DuplicateVariableClause, span, $"Duplicate variable clause '{clauseName}'.");
    }

    // Semantic diagnostics

    public void ReportSymbolAlreadyDeclared(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0201_SymbolAlreadyDeclared, span, $"Symbol '{name}' is already declared.");
    }

    public void ReportUndefinedName(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0202_UndefinedName, span, $"Name '{name}' does not exist in the current scope.");
    }

    public void ReportUndefinedType(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0203_UndefinedType, span, $"Type '{name}' is not defined.");
    }

    public void ReportCannotAssignToConstant(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0204_CannotAssignToConstant, span, $"Cannot assign to constant '{name}'.");
    }

    public void ReportTypeMismatch(TextSpan span, string expected, string actual)
    {
        Report(DiagnosticCode.E0205_TypeMismatch, span, $"Type mismatch: expected '{expected}', got '{actual}'.");
    }

    public void ReportNotCallable(TextSpan span, string typeName)
    {
        Report(DiagnosticCode.E0206_NotCallable, span, $"Expression of type '{typeName}' is not callable.");
    }

    public void ReportArgumentCountMismatch(TextSpan span, string functionName, int expected, int actual)
    {
        Report(
            DiagnosticCode.E0207_ArgumentCountMismatch,
            span,
            $"Function '{functionName}' expects {expected} argument(s), but got {actual}.");
    }

    public void ReportInvalidLoopControl(TextSpan span, string keyword)
    {
        Report(DiagnosticCode.E0208_InvalidLoopControl, span, $"'{keyword}' can only be used inside a loop.");
    }

    public void ReportInvalidBreakInRep(TextSpan span)
    {
        Report(DiagnosticCode.E0209_InvalidBreakInRepLoop, span, "'break' is not allowed inside 'rep' loops.");
    }

    public void ReportInvalidYield(TextSpan span)
    {
        Report(DiagnosticCode.E0210_InvalidYieldUsage, span, "'yield' is only allowed inside int1/int2/int3 functions.");
    }

    public void ReportInvalidYieldto(TextSpan span)
    {
        Report(DiagnosticCode.E0211_InvalidYieldtoUsage, span, "'yieldto' is not allowed in this context.");
    }

    public void ReportReturnValueCountMismatch(TextSpan span, string functionName, int expected, int actual)
    {
        Report(
            DiagnosticCode.E0212_ReturnValueCountMismatch,
            span,
            $"Function '{functionName}' returns {expected} value(s), but got {actual}.");
    }

    public void ReportReturnOutsideFunction(TextSpan span)
    {
        Report(DiagnosticCode.E0213_ReturnOutsideFunction, span, "'return' is only allowed inside a function.");
    }

    public void ReportInvalidYieldtoTarget(TextSpan span, string target)
    {
        Report(DiagnosticCode.E0214_InvalidYieldtoTarget, span, $"'{target}' is not a coroutine function.");
    }

    public void ReportInvalidLocalStorageClass(TextSpan span, string storageClass)
    {
        Report(DiagnosticCode.E0215_InvalidLocalStorageClass, span, $"Storage class '{storageClass}' is only allowed for top-level storage declarations.");
    }

    public void ReportInvalidExternScope(TextSpan span)
    {
        Report(DiagnosticCode.E0216_InvalidExternScope, span, "automatic variable cannot be extern");
    }

    public void ReportInvalidParameterStorageClass(TextSpan span, string storageClass)
    {
        Report(DiagnosticCode.E0217_InvalidParameterStorageClass, span, $"Parameter storage class '{storageClass}' is not supported.");
    }

    public void ReportPointerStorageClassRequired(TextSpan span)
    {
        Report(DiagnosticCode.E0264_PointerStorageClassRequired, span, "Pointer types must explicitly declare a storage class.");
    }

    public void ReportUnknownNamedArgument(TextSpan span, string functionName, string parameterName)
    {
        Report(
            DiagnosticCode.E0219_UnknownNamedArgument,
            span,
            $"Function '{functionName}' does not have a parameter named '{parameterName}'.");
    }

    public void ReportDuplicateNamedArgument(TextSpan span, string parameterName)
    {
        Report(DiagnosticCode.E0220_DuplicateNamedArgument, span, $"Named argument '{parameterName}' is specified more than once.");
    }

    public void ReportPositionalArgumentAfterNamed(TextSpan span, string functionName)
    {
        Report(
            DiagnosticCode.E0221_PositionalArgumentAfterNamed,
            span,
            $"Function '{functionName}' does not allow positional arguments after named arguments.");
    }

    public void ReportNamedArgumentConflictsWithPositional(TextSpan span, string parameterName)
    {
        Report(
            DiagnosticCode.E0222_NamedArgumentConflictsWithPositional,
            span,
            $"Named argument '{parameterName}' conflicts with a positional argument.");
    }

    public void ReportInvalidAddressOfTarget(TextSpan span)
    {
        Report(DiagnosticCode.E0223_InvalidAddressOfTarget, span, "Address-of requires an addressable variable or parameter.");
    }

    public void ReportInvalidExplicitCast(TextSpan span, string sourceType, string targetType)
    {
        Report(DiagnosticCode.E0224_InvalidExplicitCast, span, $"Cannot explicitly cast from '{sourceType}' to '{targetType}'.");
    }

    public void ReportBitcastSizeMismatch(TextSpan span, string sourceType, string targetType)
    {
        Report(DiagnosticCode.E0225_BitcastSizeMismatch, span, $"Cannot bitcast from '{sourceType}' to '{targetType}' because their sizes differ.");
    }

    public void ReportAddressOfRecursiveLocal(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0226_AddressOfRecursiveLocal, span, $"Cannot take the address of local '{name}' inside a recursive function.");
    }

    public void ReportMissingReturnValue(TextSpan span, string functionName)
    {
        Report(DiagnosticCode.E0227_MissingReturnValue, span, $"Function '{functionName}' must return a value on all control-flow paths.");
    }

    public void ReportReturnFromCoroutine(TextSpan span, string functionName)
    {
        Report(DiagnosticCode.E0278_ReturnFromCoroutine, span, $"Coroutine function '{functionName}' cannot return and must end in 'yieldto' on every control-flow path.");
    }


    public void ReportFileImportAliasRequired(TextSpan span)
    {
        Report(DiagnosticCode.E0228_FileImportAliasRequired, span, "File imports require an alias: use 'import \"path\" as alias;'.");
    }

    public void ReportUnknownNamedModule(TextSpan span, string moduleName)
    {
        Report(DiagnosticCode.E0229_UnknownNamedModule, span, $"Named module '{moduleName}' is not defined.");
    }

    public void ReportImportFileNotFound(TextSpan span, string path)
    {
        Report(DiagnosticCode.E0230_ImportFileNotFound, span, $"Imported file '{path}' was not found.");
    }

    public void ReportCircularImport(TextSpan span, string path)
    {
        Report(DiagnosticCode.E0231_CircularImport, span, $"Circular import detected at '{path}'.");
    }

    public void ReportEnumLiteralRequiresContext(TextSpan span, string memberName)
    {
        Report(
            DiagnosticCode.E0232_EnumLiteralRequiresContext,
            span,
            $"Enum literal '.{memberName}' requires an expected enum type.");
    }

    public void ReportBitfieldWidthOverflow(TextSpan span, string bitfieldName, string fieldName, int usedBits, int backingBits)
    {
        Report(
            DiagnosticCode.E0233_BitfieldWidthOverflow,
            span,
            $"Bitfield '{bitfieldName}' field '{fieldName}' uses {usedBits} bits, exceeding backing width {backingBits}.");
    }

    public void ReportArrayLiteralRequiresContext(TextSpan span)
    {
        Report(
            DiagnosticCode.E0234_ArrayLiteralRequiresContext,
            span,
            "Array literal requires an expected array type.");
    }

    public void ReportArrayLiteralSpreadMustBeLast(TextSpan span)
    {
        Report(
            DiagnosticCode.E0235_ArrayLiteralSpreadMustBeLast,
            span,
            "Array literal spread element must be the last element.");
    }

    public void ReportStructUnknownField(TextSpan span, string structName, string fieldName)
    {
        Report(DiagnosticCode.E0236_StructUnknownField, span, $"Struct '{structName}' does not have a field named '{fieldName}'.");
    }

    public void ReportStructMissingFields(TextSpan span, string structName, string missingFields)
    {
        Report(DiagnosticCode.E0237_StructMissingFields, span, $"Struct literal for '{structName}' is missing field(s): {missingFields}.");
    }

    public void ReportStructDuplicateField(TextSpan span, string fieldName)
    {
        Report(DiagnosticCode.E0238_StructDuplicateField, span, $"Field '{fieldName}' is specified more than once.");
    }

    public void ReportStringLengthMismatch(TextSpan span, int expectedLength, int actualLength)
    {
        Report(
            DiagnosticCode.E0239_StringLengthMismatch,
            span,
            $"String length {actualLength} does not match array length {expectedLength}.");
    }

    public void ReportStringToNonConstPointer(TextSpan span)
    {
        Report(
            DiagnosticCode.E0240_StringToNonConstPointer,
            span,
            "String literal cannot be assigned to a non-const pointer.");
    }

    public void ReportComptimeValueRequired(TextSpan span)
    {
        Report(
            DiagnosticCode.E0241_ComptimeValueRequired,
            span,
            "Expression must be compile-time evaluable in this context.");
    }

    public void ReportComptimeUnsupportedConstruct(TextSpan span, string detail)
    {
        Report(
            DiagnosticCode.E0242_ComptimeUnsupportedConstruct,
            span,
            $"Construct is not supported during comptime evaluation: {detail}");
    }

    public void ReportComptimeForbiddenSymbolAccess(TextSpan span, string detail)
    {
        Report(
            DiagnosticCode.E0243_ComptimeForbiddenSymbolAccess,
            span,
            $"Comptime evaluation cannot access this symbol: {detail}");
    }

    public void ReportComptimeFuelExhausted(TextSpan span)
    {
        Report(
            DiagnosticCode.E0244_ComptimeFuelExhausted,
            span,
            "Comptime evaluation ran out of fuel.");
    }

    public void ReportDuplicateNamedModuleRoot(TextSpan span, string firstModuleName, string secondModuleName, string path)
    {
        Report(
            DiagnosticCode.E0245_DuplicateNamedModuleRoot,
            span,
            $"Named modules '{firstModuleName}' and '{secondModuleName}' both resolve to '{path}'.");
    }

    public void ReportMultiAssignmentRequiresCall(TextSpan span)
    {
        Report(
            DiagnosticCode.E0246_MultiAssignmentRequiresCall,
            span,
            "Multi-target assignment requires a function call on the right-hand side.");
    }

    public void ReportMultiAssignmentTargetCountMismatch(TextSpan span, string functionName, int expected, int actual)
    {
        Report(
            DiagnosticCode.E0247_MultiAssignmentTargetCountMismatch,
            span,
            $"Function '{functionName}' returns {expected} value(s), but {actual} assignment target(s) provided.");
    }

    public void ReportDiscardInExpression(TextSpan span)
    {
        Report(
            DiagnosticCode.E0248_DiscardInExpression,
            span,
            "The discard '_' can only be used as an assignment target.");
    }

    public void ReportExternCannotHaveInitializer(TextSpan span, string name)
    {
        Report(
            DiagnosticCode.E0249_ExternCannotHaveInitializer,
            span,
            $"Extern variable '{name}' cannot have an initializer.");
    }

    public void ReportMemoryofRequiresVariable(TextSpan span)
    {
        Report(DiagnosticCode.E0250_MemoryofRequiresVariable, span, "'memoryof' requires a variable declaration, not a type.");
    }

    public void ReportQueryRequiresMemorySpace(TextSpan span, string operatorName)
    {
        Report(DiagnosticCode.E0251_QueryRequiresMemorySpace, span, $"'{operatorName}' of a type requires a memory space argument.");
    }

    public void ReportQueryUnsupportedType(TextSpan span, string operatorName, string typeName)
    {
        Report(DiagnosticCode.E0252_QueryUnsupportedType, span, $"Cannot determine size/alignment for type '{typeName}' in '{operatorName}'.");
    }

    public void ReportQueryAutomaticLocal(TextSpan span, string operatorName, string variableName)
    {
        Report(DiagnosticCode.E0253_QueryAutomaticLocal, span, $"'{operatorName}' cannot be applied to automatic local variable '{variableName}'.");
    }

    public void ReportInvalidMemorySpaceArgument(TextSpan span)
    {
        Report(DiagnosticCode.E0254_InvalidMemorySpaceArgument, span, "Expected a 'builtin.MemorySpace' value.");
    }

    public void ReportAssertionFailed(TextSpan span, string? message)
    {
        Report(
            DiagnosticCode.E0255_AssertionFailed,
            span,
            message is null ? "assertion failed" : $"assertion failed: {message}");
    }

    public void ReportUnknownBuiltin(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0256_UnknownBuiltin, span, $"Unknown builtin '@{name}'.");
    }

    public void ReportInvalidPointerArithmetic(TextSpan span, string operation)
    {
        Report(
            DiagnosticCode.E0257_InvalidPointerArithmetic,
            span,
            $"Invalid pointer arithmetic for '{operation}'. Only '[*]' pointers support '+', '-', '+=', and '-=' with integer deltas.");
    }

    public void ReportIncompatiblePointerSubtraction(TextSpan span, string leftType, string rightType)
    {
        Report(
            DiagnosticCode.E0258_IncompatiblePointerSubtraction,
            span,
            $"Pointer subtraction requires matching '[*]' pointer element types and memory spaces, but got '{leftType}' and '{rightType}'.");
    }

    public void ReportExpressionNotAStatement(TextSpan span)
    {
        Report(DiagnosticCode.E0259_ExpressionNotAStatement, span, "An expression is not a statement. Only function calls are allowed as standalone statements.");
    }

    public void ReportRangeIterationRequiresBinding(TextSpan span)
    {
        Report(DiagnosticCode.E0260_RangeIterationRequiresBinding, span, "Range iteration requires an index binding '-> index'.");
    }

    public void ReportRangeExpressionOutsideForLoop(TextSpan span)
    {
        Report(
            DiagnosticCode.E0263_RangeExpressionOutsideForLoop,
            span,
            "Range expressions are only allowed as the iterable of a for/rep for statement.");
    }

    public void ReportComptimeIntegerTruncation(TextSpan span, string value, string targetType, string truncatedValue)
    {
        Report(
            DiagnosticCode.W0261_ComptimeIntegerTruncation,
            span,
            $"Compile-time integer value {value} is truncated to {truncatedValue} when converted to '{targetType}'.");
    }

    public void ReportAddressOfParameter(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0262_AddressOfParameter, span, $"Cannot take the address of parameter '{name}'.");
    }

    /// <summary>
    /// Reports that an unqualified name is provided by multiple imported layouts.
    /// </summary>
    public void ReportAmbiguousLayoutMemberAccess(TextSpan span, string name, IReadOnlyList<string> layoutNames)
    {
        Report(
            DiagnosticCode.E0265_AmbiguousLayoutMemberAccess,
            span,
            $"Name '{name}' is provided by multiple layouts ({string.Join(", ", layoutNames)}). Use 'Layout.member' to disambiguate.");
    }

    /// <summary>
    /// Reports that lexical resolution won over one or more imported layout members of the same name.
    /// </summary>
    public void ReportLexicalNameConflictsWithLayoutMember(TextSpan span, string name, IReadOnlyList<string> layoutNames)
    {
        Report(
            DiagnosticCode.W0266_LexicalNameConflictsWithLayoutMember,
            span,
            $"Name '{name}' is visible both lexically and through layout members ({string.Join(", ", layoutNames)}). The lexical name wins.");
    }

    /// <summary>
    /// Reports that a layout declaration shadows a member inherited from a parent layout.
    /// </summary>
    public void ReportLayoutMemberShadowsParentMember(TextSpan span, string layoutName, string memberName)
    {
        Report(
            DiagnosticCode.W0267_LayoutMemberShadowsParentMember,
            span,
            $"Layout '{layoutName}' declares member '{memberName}', which shadows an inherited layout member.");
    }

    /// <summary>
    /// Reports that a layout attempted to inherit from a task-private layout.
    /// </summary>
    public void ReportTaskLayoutCannotBeInherited(TextSpan span, string taskName)
    {
        Report(DiagnosticCode.E0268_TaskLayoutCannotBeInherited, span, $"Task layout '{taskName}' cannot be inherited.");
    }

    /// <summary>
    /// Reports that the required root entry task exists but does not start in cog storage.
    /// </summary>
    public void ReportMainTaskMustBeCog(TextSpan span, string taskName, Blade.Semantics.VariableStorageClass storageClass)
    {
        string storageClassKeyword = storageClass switch
        {
            Blade.Semantics.VariableStorageClass.Cog => "cog",
            Blade.Semantics.VariableStorageClass.Lut => "lut",
            Blade.Semantics.VariableStorageClass.Hub => "hub",
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };

        Report(
            DiagnosticCode.W0269_MainTaskMustBeCog,
            span,
            $"Entry task '{taskName}' should be declared as 'cog task'; found '{storageClassKeyword} task'.");
    }

    /// <summary>
    /// Reports that the root module does not export the required <c>main</c> task.
    /// </summary>
    public void ReportMissingMainTask(TextSpan span)
    {
        Report(DiagnosticCode.E0270_MissingMainTask, span, "Root module must export a task named 'main'.");
    }

    /// <summary>
    /// Reports that a callee requires layouts that are not available to the caller.
    /// </summary>
    public void ReportFunctionLayoutSubsetViolation(TextSpan span, string callerName, string calleeName, IReadOnlyList<string> callerLayouts, IReadOnlyList<string> calleeLayouts)
    {
        Requires.NotNull(callerLayouts);
        Requires.NotNull(calleeLayouts);

        string callerLayoutText = callerLayouts.Count == 0 ? "<none>" : string.Join(", ", callerLayouts);
        string calleeLayoutText = calleeLayouts.Count == 0 ? "<none>" : string.Join(", ", calleeLayouts);
        Report(
            DiagnosticCode.E0271_FunctionLayoutSubsetViolation,
            span,
            $"Function '{callerName}' cannot transfer control to '{calleeName}' because callee layouts [{calleeLayoutText}] are not a subset of caller layouts [{callerLayoutText}].");
    }

    /// <summary>
    /// Reports that a function metadata block repeated the <c>layout(...)</c> property.
    /// </summary>
    public void ReportDuplicateFunctionLayoutMetadata(TextSpan span)
    {
        Report(DiagnosticCode.W0272_DuplicateFunctionLayoutMetadata, span, "Duplicate function metadata property 'layout(...)'; layouts are merged.");
    }

    /// <summary>
    /// Reports that a function metadata block repeated the <c>align(...)</c> property.
    /// </summary>
    public void ReportDuplicateFunctionAlignMetadata(TextSpan span)
    {
        Report(DiagnosticCode.E0273_DuplicateFunctionAlignMetadata, span, "Duplicate function metadata property 'align(...)'.");
    }

    /// <summary>
    /// Reports that a function metadata <c>align(...)</c> value is invalid.
    /// </summary>
    public void ReportInvalidFunctionAlignment(TextSpan span, int alignment)
    {
        Report(DiagnosticCode.E0274_InvalidFunctionAlignment, span, $"Function alignment must be a positive power of two; got '{alignment}'.");
    }

    /// <summary>
    /// Reports that a function metadata <c>layout(...)</c> reference resolved to a task-private layout.
    /// </summary>
    public void ReportTaskLayoutNotAllowedInFunctionMetadata(TextSpan span, string taskName)
    {
        Report(DiagnosticCode.E0275_TaskLayoutNotAllowedInFunctionMetadata, span, $"Task layout '{taskName}' cannot be referenced from function metadata.");
    }

    /// <summary>
    /// Reports that a qualified layout member access targets a layout that is not visible in the current context.
    /// </summary>
    public void ReportAccessToForeignLayout(TextSpan span, string layoutName, string memberName)
    {
        Report(
            DiagnosticCode.E0276_AccessToForeignLayout,
            span,
            $"Layout member '{layoutName}.{memberName}' is not accessible from this context because the layout is not declared here.");
    }

    /// <summary>
    /// Reports that a plain top-level global used a storage class that is only meaningful inside a layout.
    /// </summary>
    public void ReportUnsupportedGlobalStorage(TextSpan span, string storageClass)
    {
        Report(
            DiagnosticCode.E0277_UnsupportedGlobalStorage,
            span,
            $"Top-level global storage class '{storageClass}' is not supported here. Use 'hub' or move the declaration into a layout.");
    }

    // Inline assembly diagnostics

    public void ReportInlineAsmUnknownInstruction(TextSpan span, string mnemonic)
    {
        Report(DiagnosticCode.E0301_InlineAsmUnknownInstruction, span, $"Unknown P2 instruction '{mnemonic}' in inline assembly.");
    }

    public void ReportInlineAsmUndefinedVariable(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0302_InlineAsmUndefinedVariable, span, $"Undefined variable '{name}' referenced in inline assembly.");
    }

    public void ReportInlineAsmEmptyInstruction(TextSpan span)
    {
        Report(DiagnosticCode.E0303_InlineAsmEmptyInstruction, span, "Empty instruction in inline assembly block.");
    }

    public void ReportInlineAsmInvalidFlagOutput(TextSpan span, string flag)
    {
        Report(DiagnosticCode.E0304_InlineAsmInvalidFlagOutput, span, $"Invalid flag output '@{flag}' in inline assembly. Expected '@C' or '@Z'.");
    }

    public void ReportInlineAsmInvalidInstructionForm(TextSpan span, string mnemonic, int operandCount)
    {
        Report(
            DiagnosticCode.E0305_InlineAsmInvalidInstructionForm,
            span,
            $"Instruction '{mnemonic}' does not support {operandCount} operand(s) in inline assembly.");
    }

    public void ReportInlineAsmUndefinedLabel(TextSpan span, string name)
    {
        Report(
            DiagnosticCode.E0306_InlineAsmUndefinedLabel,
            span,
            $"Inline assembly references undefined label '{name}'. Only labels defined within the same asm block are accessible.");
    }

    public void ReportInlineAsmTempReadBeforeWrite(TextSpan span, string name)
    {
        Report(
            DiagnosticCode.W0307_InlineAsmTempReadBeforeWrite,
            span,
            $"Inline assembly temporary '{name}' is read before any prior write in the same asm block. The register contents are unspecified.");
    }

    public void ReportUnsupportedLowering(TextSpan span, string lowering)
    {
        Report(
            DiagnosticCode.E0401_UnsupportedLowering,
            span,
            $"Backend lowering for '{lowering}' is not implemented yet.");
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
