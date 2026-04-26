using System;
using System.Linq;
using System.Text;
using Blade.DiagnosticGen;
using Blade.Diagnostics;
using Blade.Source;
using DiagnosticGenProgram = Blade.DiagnosticGen.Program;

namespace Blade.Tests;

[TestFixture]
public class DiagnosticGenTests
{
    [Test]
    public void Parse_LocatedDiagnostic_WithFormattedMessageAndParameter()
    {
        Model model = Model.Parse(
        [
            "using Blade.Source",
            "located UnexpectedCharacter: E0001",
            "    message: $\"Unexpected character '{character}'\"",
            "    param: char character",
        ]);

        Message message = model.Messages.Single();
        Assert.That(message.Kind, Is.EqualTo(MessageKind.Located));
        Assert.That(message.Name, Is.EqualTo("UnexpectedCharacter"));
        Assert.That(message.Code, Is.EqualTo("E0001"));
        Assert.That(message.ClassName, Is.EqualTo("UnexpectedCharacterError"));
        Assert.That(message.Text.Expression, Is.EqualTo("$\"Unexpected character '{character}'\""));
        Assert.That(message.Text.IsInterpolated, Is.True);
        Assert.That(message.Parameters.Single(), Is.EqualTo(new MessageParameter("char", "character")));
    }

    [Test]
    public void Parse_GenericDiagnostic_WithPlainMessage()
    {
        Model model = Model.Parse(
        [
            "generic MissingMainTask: E0270",
            "    message: \"Root module must export a task named 'main'.\"",
        ]);

        Message message = model.Messages.Single();
        Assert.That(message.Kind, Is.EqualTo(MessageKind.Generic));
        Assert.That(message.ClassName, Is.EqualTo("MissingMainTaskError"));
        Assert.That(message.Text.IsInterpolated, Is.False);
    }

    [Test]
    public void Parse_MapsSeverityPrefixToClassSuffix()
    {
        Model model = Model.Parse(
        [
            "generic First: E1000",
            "    message: \"first\"",
            "generic Second: W1001",
            "    message: \"second\"",
            "generic Third: I1002",
            "    message: \"third\"",
        ]);

        Assert.That(
            model.Messages.Select(static message => message.ClassName),
            Is.EqualTo(["FirstError", "SecondWarning", "ThirdNote"]));
    }

    [Test]
    public void Parse_RejectsDuplicateCode()
    {
        FormatException exception = Assert.Throws<FormatException>(
            static () => Model.Parse(
            [
                "generic First: E1000",
                "    message: \"first\"",
                "generic Second: E1000",
                "    message: \"second\"",
            ]))!;

        Assert.That(exception.Message, Does.Contain("Duplicate diagnostic code"));
    }

    [Test]
    public void Parse_RejectsDuplicateName()
    {
        FormatException exception = Assert.Throws<FormatException>(
            static () => Model.Parse(
            [
                "generic First: E1000",
                "    message: \"first\"",
                "generic First: W1001",
                "    message: \"second\"",
            ]))!;

        Assert.That(exception.Message, Does.Contain("Duplicate diagnostic name"));
    }

    [Test]
    public void Parse_RejectsInvalidCodeFormat()
    {
        FormatException exception = Assert.Throws<FormatException>(
            static () => Model.Parse(
            [
                "generic First: X1000",
                "    message: \"first\"",
            ]))!;

        Assert.That(exception.Message, Does.Contain("Invalid diagnostic code"));
    }

    [Test]
    public void Parse_RejectsMissingMessage()
    {
        FormatException exception = Assert.Throws<FormatException>(
            static () => Model.Parse(["generic First: E1000"]))!;

        Assert.That(exception.Message, Does.Contain("missing a message"));
    }

    [Test]
    public void Parse_RejectsDuplicateParameterName()
    {
        FormatException exception = Assert.Throws<FormatException>(
            static () => Model.Parse(
            [
                "generic First: E1000",
                "    message: $\"{value}\"",
                "    param: int value",
                "    param: string value",
            ]))!;

        Assert.That(exception.Message, Does.Contain("already has a parameter"));
    }

    [Test]
    public void Parse_PreservesCommentMarkerInsideString()
    {
        Model model = Model.Parse(
        [
            "generic First: E1000",
            "    message: \"value # still text\" # line comment",
        ]);

        Assert.That(model.Messages.Single().Text.Expression, Is.EqualTo("\"value # still text\""));
    }

    [Test]
    public void Program_GeneratesDiagnosticClasses()
    {
        using TempDirectory temp = new();
        temp.WriteFile(
            "Messages.def",
            string.Join(
                Environment.NewLine,
                [
                    "using Blade.Source",
                    "located UnexpectedCharacter: E0001",
                    "    message: $\"Unexpected character '{character}'\"",
                    "    param: char character",
                ]));

        int exitCode = DiagnosticGenProgram.Main(
        [
            temp.GetFullPath("Messages.def"),
            temp.GetFullPath("DiagnosticMessages.g.cs"),
        ]);

        string generated = temp.ReadFile("DiagnosticMessages.g.cs", Encoding.UTF8);
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(generated, Does.Contain("using Blade.Source;"));
        Assert.That(generated, Does.Contain("public sealed class UnexpectedCharacterError(SourceText source, TextSpan span, char character)"));
        Assert.That(generated, Does.Contain(": LocatedDiagnosticMessage(source, span, \"UnexpectedCharacter\", DiagnosticSeverity.Error, 1)"));
        Assert.That(generated, Does.Contain("public char Character { get; } = character;"));
        Assert.That(generated, Does.Contain("protected override global::System.FormattableString GetFormattableMessage()"));
    }

    [Test]
    public void DiagnosticBag_ReportDiagnosticMessage_StoresLocatedDiagnostic()
    {
        DiagnosticBag bag = new();
        SourceText source = new("abc", "sample.blade");
        TextSpan span = new(1, 1);

        bag.Report(new UnexpectedCharacterError(source, span, 'X'));

        Diagnostic diagnostic = bag.Single();
        Assert.That(diagnostic.Source, Is.SameAs(source));
        Assert.That(diagnostic.Span, Is.EqualTo(span));
        Assert.That(diagnostic.DiagnosticMessage.Code, Is.EqualTo(1));
        Assert.That(diagnostic.DiagnosticMessage.Name, Is.EqualTo("UnexpectedCharacter"));
        Assert.That(diagnostic.DiagnosticMessage.Severity, Is.EqualTo(DiagnosticSeverity.Error));
        Assert.That(diagnostic.Message, Is.EqualTo("Unexpected character 'X'."));
        Assert.That(diagnostic.IsLocated, Is.True);
    }

    [Test]
    public void DiagnosticBag_ReportDiagnosticMessage_StoresGenericDiagnostic()
    {
        DiagnosticBag bag = new();

        bag.Report(new GenericTestNote());

        Diagnostic diagnostic = bag.Single();
        Assert.That(diagnostic.DiagnosticMessage.Code, Is.EqualTo(9002));
        Assert.That(diagnostic.DiagnosticMessage.Name, Is.EqualTo("GenericTest"));
        Assert.That(diagnostic.DiagnosticMessage.Severity, Is.EqualTo(DiagnosticSeverity.Note));
        Assert.That(diagnostic.Message, Is.EqualTo("note"));
        Assert.That(diagnostic.IsLocated, Is.False);
    }

    [Test]
    public void DiagnosticMessageLookupHelpers_ReturnGeneratedMetadata()
    {
        Assert.That(DiagnosticMessage.GetByName("UnexpectedCharacter"), Is.EqualTo((DiagnosticSeverity.Error, 1)));
        Assert.That(DiagnosticMessage.GetNameFrom(307), Is.EqualTo("InlineAsmTempReadBeforeWrite"));
        Assert.That(DiagnosticMessage.GetSeverity(307), Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(DiagnosticMessage.GetSeverity("InlineAsmTempReadBeforeWrite"), Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(DiagnosticMessage.GetByName("missing"), Is.Null);
        Assert.That(DiagnosticMessage.GetNameFrom(123456), Is.Null);
        Assert.That(DiagnosticMessage.GetSeverity(123456), Is.Null);
        Assert.That(DiagnosticMessage.GetSeverity("missing"), Is.Null);
    }

    private sealed class GenericTestNote() : DiagnosticMessage("GenericTest", DiagnosticSeverity.Note, 9002, "note")
    {
        public override bool IsLocated => false;
    }
}
