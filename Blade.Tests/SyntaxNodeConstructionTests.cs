using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Tests;

[TestFixture]
public class SyntaxNodeConstructionTests
{
    private static Token Tok(TokenKind kind, int start, int len, string text, object? value = null)
        => new(kind, new TextSpan(start, len), text, value);

    [Test]
    public void TypeConstructors_InitializeAllAggregateTypeNodes()
    {
        PrimitiveTypeSyntax backing = new(Tok(TokenKind.U32Keyword, 10, 3, "u32"));
        StructFieldSyntax field = new(Tok(TokenKind.Identifier, 20, 2, "lo"), Tok(TokenKind.Colon, 22, 1, ":"), backing);
        EnumMemberSyntax member = new(Tok(TokenKind.Identifier, 30, 4, "Idle"), null, null);

        SeparatedSyntaxList<StructFieldSyntax> structFields = new([field]);
        SeparatedSyntaxList<EnumMemberSyntax> enumMembers = new([member]);

        UnionTypeSyntax unionType = new(Tok(TokenKind.UnionKeyword, 0, 5, "union"), Tok(TokenKind.OpenBrace, 6, 1, "{"), structFields, Tok(TokenKind.CloseBrace, 40, 1, "}"));
        EnumTypeSyntax enumType = new(Tok(TokenKind.EnumKeyword, 0, 4, "enum"), Tok(TokenKind.OpenParen, 5, 1, "("), backing, Tok(TokenKind.CloseParen, 9, 1, ")"), Tok(TokenKind.OpenBrace, 11, 1, "{"), enumMembers, Tok(TokenKind.CloseBrace, 41, 1, "}"));
        BitfieldTypeSyntax bitfieldType = new(Tok(TokenKind.BitfieldKeyword, 0, 8, "bitfield"), Tok(TokenKind.OpenParen, 8, 1, "("), backing, Tok(TokenKind.CloseParen, 12, 1, ")"), Tok(TokenKind.OpenBrace, 14, 1, "{"), structFields, Tok(TokenKind.CloseBrace, 42, 1, "}"));
        MultiPointerTypeSyntax multiPointer = new(Tok(TokenKind.OpenBracket, 0, 1, "["), Tok(TokenKind.Star, 1, 1, "*"), Tok(TokenKind.CloseBracket, 2, 1, "]"), Tok(TokenKind.RegKeyword, 3, 3, "reg"), null, null, null, backing);
        QualifiedTypeSyntax qualified = new([Tok(TokenKind.Identifier, 50, 3, "mod"), Tok(TokenKind.Identifier, 54, 4, "Type")]);

        Assert.That(unionType.UnionKeyword.Kind, Is.EqualTo(TokenKind.UnionKeyword));
        Assert.That(unionType.OpenBrace.Kind, Is.EqualTo(TokenKind.OpenBrace));
        Assert.That(unionType.Fields.Count, Is.EqualTo(1));
        Assert.That(unionType.CloseBrace.Kind, Is.EqualTo(TokenKind.CloseBrace));

        Assert.That(enumType.EnumKeyword.Kind, Is.EqualTo(TokenKind.EnumKeyword));
        Assert.That(enumType.OpenParen.Kind, Is.EqualTo(TokenKind.OpenParen));
        Assert.That(enumType.BackingType, Is.SameAs(backing));
        Assert.That(enumType.CloseParen.Kind, Is.EqualTo(TokenKind.CloseParen));
        Assert.That(enumType.OpenBrace.Kind, Is.EqualTo(TokenKind.OpenBrace));
        Assert.That(enumType.Members.Count, Is.EqualTo(1));
        Assert.That(enumType.CloseBrace.Kind, Is.EqualTo(TokenKind.CloseBrace));

        Assert.That(bitfieldType.BitfieldKeyword.Kind, Is.EqualTo(TokenKind.BitfieldKeyword));
        Assert.That(bitfieldType.OpenParen.Kind, Is.EqualTo(TokenKind.OpenParen));
        Assert.That(bitfieldType.BackingType, Is.SameAs(backing));
        Assert.That(bitfieldType.CloseParen.Kind, Is.EqualTo(TokenKind.CloseParen));
        Assert.That(bitfieldType.OpenBrace.Kind, Is.EqualTo(TokenKind.OpenBrace));
        Assert.That(bitfieldType.Fields.Count, Is.EqualTo(1));
        Assert.That(bitfieldType.CloseBrace.Kind, Is.EqualTo(TokenKind.CloseBrace));

        Assert.That(multiPointer.OpenBracket.Kind, Is.EqualTo(TokenKind.OpenBracket));
        Assert.That(multiPointer.Star.Kind, Is.EqualTo(TokenKind.Star));
        Assert.That(multiPointer.CloseBracket.Kind, Is.EqualTo(TokenKind.CloseBracket));
        Assert.That(multiPointer.StorageClassKeyword?.Kind, Is.EqualTo(TokenKind.RegKeyword));
        Assert.That(multiPointer.ConstKeyword, Is.Null);
        Assert.That(multiPointer.VolatileKeyword, Is.Null);
        Assert.That(multiPointer.AlignClause, Is.Null);
        Assert.That(multiPointer.PointeeType, Is.SameAs(backing));

        Assert.That(qualified.Parts.Count, Is.EqualTo(2));
        Assert.That(qualified.Parts[0].Text, Is.EqualTo("mod"));
        Assert.That(qualified.Parts[1].Text, Is.EqualTo("Type"));
    }

    [Test]
    public void ExpressionConstructors_InitializeArrayTypedStructAndEnumLiteral()
    {
        LiteralExpressionSyntax one = new(Tok(TokenKind.IntegerLiteral, 0, 1, "1", 1));
        ArrayElementSyntax spreadElement = new(one, Tok(TokenKind.DotDotDot, 2, 3, "..."));
        ArrayLiteralExpressionSyntax array = new(Tok(TokenKind.OpenBracket, 0, 1, "["), new SeparatedSyntaxList<ArrayElementSyntax>([spreadElement]), Tok(TokenKind.CloseBracket, 6, 1, "]"));

        FieldInitializerSyntax init = new(Tok(TokenKind.Dot, 10, 1, "."), Tok(TokenKind.Identifier, 11, 1, "x"), Tok(TokenKind.Equal, 13, 1, "="), one);
        TypedStructLiteralExpressionSyntax typed = new(new NameExpressionSyntax(Tok(TokenKind.Identifier, 20, 5, "Point")), Tok(TokenKind.OpenBrace, 26, 1, "{"), new SeparatedSyntaxList<FieldInitializerSyntax>([init]), Tok(TokenKind.CloseBrace, 40, 1, "}"));

        EnumLiteralExpressionSyntax enumLiteral = new(Tok(TokenKind.Dot, 50, 1, "."), Tok(TokenKind.Identifier, 51, 4, "Busy"));

        Assert.That(array.Elements[0].Spread, Is.Not.Null);
        Assert.That(typed.Initializers.Count, Is.EqualTo(1));
        Assert.That(enumLiteral.MemberName.Text, Is.EqualTo("Busy"));
    }

    [Test]
    public void AsmFunctionDeclarationConstructor_StoresBodyAndReturnSpec()
    {
        PrimitiveTypeSyntax boolType = new(Tok(TokenKind.BoolKeyword, 10, 4, "bool"));
        FlagAnnotationSyntax flag = new(Tok(TokenKind.At, 14, 1, "@"), Tok(TokenKind.Identifier, 15, 1, "C"));
        ReturnItemSyntax ret = new(null, null, boolType, flag);

        AsmFunctionDeclarationSyntax asmFunction = new(
            Tok(TokenKind.AsmKeyword, 0, 3, "asm"),
            Tok(TokenKind.VolatileKeyword, 4, 8, "volatile"),
            Tok(TokenKind.FnKeyword, 13, 2, "fn"),
            Tok(TokenKind.Identifier, 16, 4, "demo"),
            Tok(TokenKind.OpenParen, 20, 1, "("),
            new SeparatedSyntaxList<ParameterSyntax>([]),
            Tok(TokenKind.CloseParen, 21, 1, ")"),
            Tok(TokenKind.Arrow, 23, 2, "->"),
            new SeparatedSyntaxList<ReturnItemSyntax>([ret]),
            Tok(TokenKind.OpenBrace, 26, 1, "{"),
            "MOV {return}, #1",
            Tok(TokenKind.CloseBrace, 50, 1, "}"));

        Assert.That(asmFunction.AsmKeyword.Kind, Is.EqualTo(TokenKind.AsmKeyword));
        Assert.That(asmFunction.VolatileKeyword?.Kind, Is.EqualTo(TokenKind.VolatileKeyword));
        Assert.That(asmFunction.FnKeyword.Kind, Is.EqualTo(TokenKind.FnKeyword));
        Assert.That(asmFunction.Name.Text, Is.EqualTo("demo"));
        Assert.That(asmFunction.OpenParen.Kind, Is.EqualTo(TokenKind.OpenParen));
        Assert.That(asmFunction.Parameters.Count, Is.EqualTo(0));
        Assert.That(asmFunction.CloseParen.Kind, Is.EqualTo(TokenKind.CloseParen));
        Assert.That(asmFunction.Arrow?.Kind, Is.EqualTo(TokenKind.Arrow));
        Assert.That(asmFunction.ReturnSpec, Is.Not.Null);
        Assert.That(asmFunction.OpenBrace.Kind, Is.EqualTo(TokenKind.OpenBrace));
        Assert.That(asmFunction.Body, Does.Contain("MOV"));
        Assert.That(asmFunction.CloseBrace.Kind, Is.EqualTo(TokenKind.CloseBrace));
    }

    [Test]
    public void AuxiliaryNodeConstructors_ExposeAllProperties()
    {
        PrimitiveTypeSyntax u32 = new(Tok(TokenKind.U32Keyword, 0, 3, "u32"));
        LiteralExpressionSyntax literal = new(Tok(TokenKind.IntegerLiteral, 4, 1, "1", 1));

        AlignClauseSyntax align = new(Tok(TokenKind.AlignKeyword, 6, 5, "align"), Tok(TokenKind.OpenParen, 11, 1, "("), literal, Tok(TokenKind.CloseParen, 13, 1, ")"));
        AddressClauseSyntax address = new(Tok(TokenKind.At, 14, 1, "@"), Tok(TokenKind.OpenParen, 15, 1, "("), literal, Tok(TokenKind.CloseParen, 17, 1, ")"));
        StructFieldSyntax field = new(Tok(TokenKind.Identifier, 18, 1, "x"), Tok(TokenKind.Colon, 19, 1, ":"), u32);
        FlagAnnotationSyntax flag = new(Tok(TokenKind.At, 20, 1, "@"), Tok(TokenKind.Identifier, 21, 1, "C"));
        AsmOutputBindingSyntax asmBinding = new(Tok(TokenKind.Arrow, 26, 2, "->"), Tok(TokenKind.Identifier, 28, 5, "state"), Tok(TokenKind.Colon, 33, 1, ":"), u32, flag);
        EnumMemberSyntax enumOpen = new(Tok(TokenKind.DotDotDot, 34, 3, "..."), null, null, isOpenMarker: true);
        EnumMemberSyntax enumAssigned = new(Tok(TokenKind.Identifier, 38, 1, "A"), Tok(TokenKind.Equal, 39, 1, "="), literal);
        ForBindingSyntax binding = new(Tok(TokenKind.Arrow, 40, 2, "->"), Tok(TokenKind.Ampersand, 42, 1, "&"), Tok(TokenKind.Identifier, 43, 1, "i"), Tok(TokenKind.Comma, 44, 1, ","), Tok(TokenKind.Identifier, 45, 1, "j"));

        Assert.That(align.Alignment, Is.SameAs(literal));
        Assert.That(address.Address, Is.SameAs(literal));
        Assert.That(field.Type, Is.SameAs(u32));
        Assert.That(asmBinding.FlagAnnotation, Is.SameAs(flag));
        Assert.That(enumOpen.IsOpenMarker, Is.True);
        Assert.That(enumAssigned.Value, Is.SameAs(literal));
        Assert.That(binding.IndexName?.Text, Is.EqualTo("j"));
    }

}
