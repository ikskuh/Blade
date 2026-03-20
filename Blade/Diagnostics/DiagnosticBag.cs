using System.Collections;
using System.Collections.Generic;
using Blade.Source;

namespace Blade.Diagnostics;

/// <summary>
/// Collects diagnostics during compilation.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = new();

    public int Count => _diagnostics.Count;

    public bool HasErrors => _diagnostics.Count > 0;

    public void Report(DiagnosticCode code, TextSpan span, string message)
    {
        _diagnostics.Add(new Diagnostic(code, span, message));
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

    // Semantic diagnostics

    public void ReportSymbolAlreadyDeclared(TextSpan span, string name)
    {
        Report(DiagnosticCode.E0201_SymbolAlreadyDeclared, span, $"Symbol '{name}' is already declared in this scope.");
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
        Report(DiagnosticCode.E0211_InvalidYieldtoUsage, span, "'yieldto' is only allowed at top-level or inside coro functions.");
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
        Report(DiagnosticCode.E0216_InvalidExternScope, span, "'extern' is only allowed on top-level storage declarations.");
    }

    public void ReportInvalidParameterStorageClass(TextSpan span, string storageClass)
    {
        Report(DiagnosticCode.E0217_InvalidParameterStorageClass, span, $"Parameter storage class '{storageClass}' is not supported.");
    }

    public void ReportUnsupportedStorageClass(TextSpan span, string storageClass)
    {
        Report(DiagnosticCode.E0218_UnsupportedStorageClass, span, $"Storage class '{storageClass}' is not supported yet.");
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

    public void ReportUnsupportedLowering(TextSpan span, string lowering)
    {
        Report(
            DiagnosticCode.E0401_UnsupportedLowering,
            span,
            $"Backend lowering for '{lowering}' is not implemented yet.");
    }

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
