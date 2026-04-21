using System;
using System.Collections.Generic;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Tests;

[TestFixture]
public sealed class RuntimeTypeAndValueTests
{
    private static readonly TextSpan Span = new(0, 0);

    [TestCase(BuiltinTypeCase.Bit, 1)]
    [TestCase(BuiltinTypeCase.Nit, 1)]
    [TestCase(BuiltinTypeCase.Nib, 4)]
    [TestCase(BuiltinTypeCase.U8, 8)]
    [TestCase(BuiltinTypeCase.I8, 8)]
    [TestCase(BuiltinTypeCase.U16, 16)]
    [TestCase(BuiltinTypeCase.I16, 16)]
    [TestCase(BuiltinTypeCase.U32, 32)]
    [TestCase(BuiltinTypeCase.I32, 32)]
    [TestCase(BuiltinTypeCase.Uint, 32)]
    [TestCase(BuiltinTypeCase.Int, 32)]
    public void ScalarWidthBits_ReturnsExpectedWidth(BuiltinTypeCase typeCase, int expectedWidth)
    {
        RuntimeTypeSymbol type = GetBuiltinRuntimeType(typeCase);

        Assert.That(type.ScalarWidthBits, Is.EqualTo(expectedWidth));
    }

    [Test]
    public void PointerTypeNames_IncludeQualifiersInNormalizedOrder()
    {
        PointerTypeSymbol pointer = new(BuiltinTypes.U32, isConst: true, isVolatile: true, alignment: 4, storageClass: VariableStorageClass.Hub);
        MultiPointerTypeSymbol multiPointer = new(BuiltinTypes.U8, isConst: false, isVolatile: true, alignment: 8, storageClass: VariableStorageClass.Reg);

        Assert.That(pointer.Name, Is.EqualTo("*hub const volatile align(4) u32"));
        Assert.That(multiPointer.Name, Is.EqualTo("[*]reg volatile align(8) u8"));
        Assert.That(multiPointer.ScalarWidthBits, Is.EqualTo(32));
    }

    [Test]
    public void ArrayTypeNames_ReflectKnownAndUnknownLengths()
    {
        ArrayTypeSymbol known = new(BuiltinTypes.U8, 4);
        ArrayTypeSymbol unknown = new(BuiltinTypes.U16);

        Assert.That(known.Name, Is.EqualTo("[4]u8"));
        Assert.That(unknown.Name, Is.EqualTo("[u16]"));
    }

    [Test]
    public void StructTypeSymbol_UsesProvidedMembers()
    {
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal)
        {
            ["lo"] = new AggregateMemberSymbol("lo", BuiltinTypes.U16, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
            ["hi"] = new AggregateMemberSymbol("hi", BuiltinTypes.U16, byteOffset: 2, bitOffset: 0, bitWidth: 0, isBitfield: false),
        };

        StructTypeSymbol pair = new(
            "Pair",
            new Dictionary<string, BladeType>
            {
                ["lo"] = BuiltinTypes.U16,
                ["hi"] = BuiltinTypes.U16,
            },
            members,
            sizeBytes: 4,
            alignmentBytes: 2);

        Assert.That(pair.Members.Keys, Is.EquivalentTo(new[] { "lo", "hi" }));
        Assert.That(pair.Members["lo"].ByteOffset, Is.EqualTo(0));
        Assert.That(pair.Members["hi"].ByteOffset, Is.EqualTo(2));
    }

    [Test]
    public void BitfieldFieldWidthBits_ReturnExpectedResults()
    {
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U16, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new(
            "Flags",
            BuiltinTypes.U32,
            new Dictionary<string, BladeType> { ["flag"] = BuiltinTypes.Bool, ["mode"] = enumType },
            new Dictionary<string, AggregateMemberSymbol>
            {
                ["flag"] = new AggregateMemberSymbol("flag", BuiltinTypes.Bool, 0, 0, 1, isBitfield: true),
                ["mode"] = new AggregateMemberSymbol("mode", enumType, 0, 1, 16, isBitfield: true),
            });

        Assert.That(BuiltinTypes.Bool.BitfieldFieldWidthBits, Is.EqualTo(1));
        Assert.That(BuiltinTypes.U16.BitfieldFieldWidthBits, Is.EqualTo(16));
        Assert.That(enumType.BitfieldFieldWidthBits, Is.EqualTo(16));
        Assert.That(bitfieldType.BitfieldFieldWidthBits, Is.EqualTo(32));
    }

    [Test]
    public void RuntimeTypeLayout_UsesInstanceProperties()
    {
        StructTypeSymbol structType = new("S", new Dictionary<string, BladeType>(), new Dictionary<string, AggregateMemberSymbol>(), sizeBytes: 4, alignmentBytes: 2);
        UnionTypeSymbol unionType = new("U", new Dictionary<string, BladeType>(), new Dictionary<string, AggregateMemberSymbol>(), sizeBytes: 8, alignmentBytes: 4);
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U16, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U32, new Dictionary<string, BladeType>(), new Dictionary<string, AggregateMemberSymbol>());
        PointerTypeSymbol pointerType = new(BuiltinTypes.U32, isConst: false, storageClass: VariableStorageClass.Reg);
        ArrayTypeSymbol arrayType = new(BuiltinTypes.U16, 3);

        Assert.Multiple(() =>
        {
            Assert.That(structType.AlignmentBytes, Is.EqualTo(2));
            Assert.That(structType.SizeBytes, Is.EqualTo(4));
            Assert.That(unionType.AlignmentBytes, Is.EqualTo(4));
            Assert.That(unionType.SizeBytes, Is.EqualTo(8));
            Assert.That(enumType.AlignmentBytes, Is.EqualTo(2));
            Assert.That(enumType.SizeBytes, Is.EqualTo(2));
            Assert.That(bitfieldType.AlignmentBytes, Is.EqualTo(4));
            Assert.That(bitfieldType.SizeBytes, Is.EqualTo(4));
            Assert.That(pointerType.AlignmentBytes, Is.EqualTo(4));
            Assert.That(pointerType.SizeBytes, Is.EqualTo(4));
            Assert.That(arrayType.AlignmentBytes, Is.EqualTo(2));
            Assert.That(arrayType.SizeBytes, Is.EqualTo(6));
            Assert.That(BuiltinTypes.Bool.AlignmentBytes, Is.EqualTo(1));
            Assert.That(BuiltinTypes.Nib.SizeBytes, Is.EqualTo(1));
            Assert.That(arrayType.GetSizeInMemorySpace(VariableStorageClass.Hub), Is.EqualTo(6));
            Assert.That(arrayType.GetSizeInMemorySpace(VariableStorageClass.Reg), Is.EqualTo(2));
            Assert.That(arrayType.GetAlignmentInMemorySpace(VariableStorageClass.Hub), Is.EqualTo(2));
            Assert.That(arrayType.GetAlignmentInMemorySpace(VariableStorageClass.Lut), Is.EqualTo(1));
        });
    }

    [Test]
    public void RuntimeTypeLegality_RejectsRepairStylePayloads()
    {
        PointerTypeSymbol pointerType = new(BuiltinTypes.U32, isConst: false, storageClass: VariableStorageClass.Reg);
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U8, new Dictionary<string, long> { ["Idle"] = 0 }, isOpen: true);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U16, new Dictionary<string, BladeType>(), new Dictionary<string, AggregateMemberSymbol>());

        Assert.Multiple(() =>
        {
            Assert.That(pointerType.IsLegalRuntimeObject(257), Is.False);
            Assert.That(enumType.IsLegalRuntimeObject(258), Is.False);
            Assert.That(bitfieldType.IsLegalRuntimeObject(0x1_0001), Is.False);
            Assert.That(BuiltinTypes.I8.IsLegalRuntimeObject(255), Is.False);
            Assert.That(BuiltinTypes.I32.IsLegalRuntimeObject(0xFFFF_FFFFu), Is.False);
            Assert.That(BuiltinTypes.Bool.IsLegalRuntimeObject(1), Is.False);
        });
    }

    [Test]
    public void IntegerRuntimeLegality_UsesNumericRangeInsteadOfClrType()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BuiltinTypes.I16.IsLegalRuntimeObject(255L), Is.True);
            Assert.That(BuiltinTypes.U8.IsLegalRuntimeObject(255L), Is.True);
            Assert.That(BuiltinTypes.U8.IsLegalRuntimeObject(256L), Is.False);
            Assert.That(BuiltinTypes.I8.IsLegalRuntimeObject(-129L), Is.False);
        });
    }

    [Test]
    public void SignedIntegerAndScalarCastProperties_ReturnExpectedResults()
    {
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U8, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U32, new Dictionary<string, BladeType>(), new Dictionary<string, AggregateMemberSymbol>());

        Assert.Multiple(() =>
        {
            Assert.That(BuiltinTypes.Nit.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.I8.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.I16.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.I32.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.Int.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.U32.IsSignedInteger, Is.False);

            Assert.That(BuiltinTypes.Bool.IsScalarCastType, Is.True);
            Assert.That(new PointerTypeSymbol(BuiltinTypes.U32, isConst: false, storageClass: VariableStorageClass.Reg).IsScalarCastType, Is.True);
            Assert.That(enumType.IsScalarCastType, Is.True);
            Assert.That(bitfieldType.IsScalarCastType, Is.True);
            Assert.That(BladeValue.U8Array([120]).Type, Is.TypeOf<ArrayTypeSymbol>());
        });
    }

    [Test]
    public void BladeValueTypes_ValidatePayloadKinds()
    {
        RuntimeBladeValue runtimeValue = new(BuiltinTypes.I8, -1L);
        RuntimeBladeValue widenedRuntimeValue = new(BuiltinTypes.I16, 7L);
        RuntimeBladeValue byteArrayValue = BladeValue.U8Array([104, 101, 108, 108, 111]);
        ComptimeBladeValue voidValue = new((ComptimeTypeSymbol)BuiltinTypes.Void, VoidValue.Instance);
        ComptimeBladeValue undefinedValue = new((ComptimeTypeSymbol)BuiltinTypes.UndefinedLiteral, UndefinedValue.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(runtimeValue.Type, Is.SameAs(BuiltinTypes.I8));
            Assert.That(runtimeValue.Value, Is.EqualTo(-1L));
            Assert.That(widenedRuntimeValue.Type, Is.SameAs(BuiltinTypes.I16));
            Assert.That(widenedRuntimeValue.Value, Is.TypeOf<long>());
            Assert.That(widenedRuntimeValue.Value, Is.EqualTo(7L));
            Assert.That(byteArrayValue.Type, Is.TypeOf<ArrayTypeSymbol>());
            Assert.That(((ArrayTypeSymbol)byteArrayValue.Type).Length, Is.EqualTo(5));
            Assert.That(((ArrayTypeSymbol)byteArrayValue.Type).ElementType, Is.SameAs(BuiltinTypes.U8));
            Assert.That(byteArrayValue.TryGetU8Array(out byte[] bytes), Is.True);
            Assert.That(bytes, Is.EqualTo(new byte[] { 104, 101, 108, 108, 111 }));
            Assert.That(voidValue.Value, Is.SameAs(VoidValue.Instance));
            Assert.That(undefinedValue.Value, Is.SameAs(UndefinedValue.Instance));
            Assert.That(() => new RuntimeBladeValue(BuiltinTypes.U8, 256L), Throws.ArgumentException);
        });
    }

    [Test]
    public void TypeEquality_UsesStructuralSemanticsForRuntimeShapes()
    {
        ArrayTypeSymbol arrayLeft = new(BuiltinTypes.U8, 8);
        ArrayTypeSymbol arrayRight = new(BuiltinTypes.U8, 8);
        PointerTypeSymbol pointerLeft = new(BuiltinTypes.U16, isConst: true, isVolatile: true, alignment: 4, storageClass: VariableStorageClass.Hub);
        PointerTypeSymbol pointerRight = new(BuiltinTypes.U16, isConst: true, isVolatile: true, alignment: 4, storageClass: VariableStorageClass.Hub);
        MultiPointerTypeSymbol differentFamily = new(BuiltinTypes.U16, isConst: true, isVolatile: true, alignment: 4, storageClass: VariableStorageClass.Hub);

        Assert.Multiple(() =>
        {
            Assert.That(arrayLeft == arrayRight, Is.True);
            Assert.That(arrayLeft.Equals(arrayRight), Is.True);
            Assert.That(arrayLeft.GetHashCode(), Is.EqualTo(arrayRight.GetHashCode()));

            Assert.That(pointerLeft == pointerRight, Is.True);
            Assert.That(pointerLeft.GetHashCode(), Is.EqualTo(pointerRight.GetHashCode()));
            Assert.That(pointerLeft == differentFamily, Is.False);

            Assert.That(BuiltinTypes.Bool == BoolTypeSymbol.Instance, Is.True);
            Assert.That(BuiltinTypes.IntegerLiteral == IntegerLiteralTypeSymbol.Instance, Is.True);
        });
    }

    [Test]
    public void TypeEquality_KeepsNominalTypesIdentityBased()
    {
        Dictionary<string, BladeType> fields = new(StringComparer.Ordinal)
        {
            ["value"] = BuiltinTypes.U16,
        };
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal)
        {
            ["value"] = new AggregateMemberSymbol("value", BuiltinTypes.U16, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
        };
        StructTypeSymbol structLeft = new("S", fields, members, sizeBytes: 2, alignmentBytes: 2);
        StructTypeSymbol structRight = new("S", fields, members, sizeBytes: 2, alignmentBytes: 2);
        UnionTypeSymbol unionLeft = new("U", fields, members, sizeBytes: 2, alignmentBytes: 2);
        UnionTypeSymbol unionRight = new("U", fields, members, sizeBytes: 2, alignmentBytes: 2);
        EnumTypeSymbol enumLeft = new("Mode", BuiltinTypes.U16, new Dictionary<string, long>(), isOpen: false);
        EnumTypeSymbol enumRight = new("Mode", BuiltinTypes.U16, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldLeft = new("Flags", BuiltinTypes.U16, fields, members);
        BitfieldTypeSymbol bitfieldRight = new("Flags", BuiltinTypes.U16, fields, members);

        Assert.Multiple(() =>
        {
            Assert.That(structLeft == structRight, Is.False);
            Assert.That(unionLeft == unionRight, Is.False);
            Assert.That(enumLeft == enumRight, Is.False);
            Assert.That(bitfieldLeft == bitfieldRight, Is.False);
        });
    }

    [Test]
    public void TypeEquality_UsesWrappedDeclarationIdentityForFunctionAndModuleTypes()
    {
        FunctionSymbol sharedFunction = new("demo", FunctionKind.Default, isTopLevel: false);
        FunctionTypeSymbol functionLeft = new(sharedFunction);
        FunctionTypeSymbol functionRight = new(sharedFunction);
        FunctionTypeSymbol otherFunction = new(new FunctionSymbol("demo", FunctionKind.Default, isTopLevel: false));

        BoundModule sharedModule = CreateImportedModule("shared");
        ModuleSymbol sharedModuleSymbol = new("math", sharedModule);
        ModuleTypeSymbol moduleLeft = new(sharedModuleSymbol);
        ModuleTypeSymbol moduleRight = new(sharedModuleSymbol);
        ModuleTypeSymbol otherModule = new(new ModuleSymbol("math", CreateImportedModule("other")));

        Assert.Multiple(() =>
        {
            Assert.That(functionLeft == functionRight, Is.True);
            Assert.That(functionLeft.GetHashCode(), Is.EqualTo(functionRight.GetHashCode()));
            Assert.That(functionLeft == otherFunction, Is.False);

            Assert.That(moduleLeft == moduleRight, Is.True);
            Assert.That(moduleLeft.GetHashCode(), Is.EqualTo(moduleRight.GetHashCode()));
            Assert.That(moduleLeft == otherModule, Is.False);
        });
    }

    [Test]
    public void BladeValueEquality_UsesSemanticValueComparison()
    {
        BladeValue firstArray = BladeValue.U8Array([1, 2, 3]);
        BladeValue secondArray = BladeValue.U8Array([1, 2, 3]);
        BladeValue aggregateLeft = new RuntimeBladeValue(
            new StructTypeSymbol(
                "Pair",
                new Dictionary<string, BladeType>(StringComparer.Ordinal)
                {
                    ["lo"] = BuiltinTypes.U8,
                    ["hi"] = BuiltinTypes.U8,
                },
                new Dictionary<string, AggregateMemberSymbol>(StringComparer.Ordinal)
                {
                    ["lo"] = new AggregateMemberSymbol("lo", BuiltinTypes.U8, 0, 0, 0, isBitfield: false),
                    ["hi"] = new AggregateMemberSymbol("hi", BuiltinTypes.U8, 1, 0, 0, isBitfield: false),
                },
                sizeBytes: 2,
                alignmentBytes: 1),
            new Dictionary<string, BladeValue>(StringComparer.Ordinal)
            {
                ["lo"] = BladeValue.U8(1),
                ["hi"] = BladeValue.U8(2),
            });
        BladeValue aggregateRight = new RuntimeBladeValue(
            (RuntimeTypeSymbol)aggregateLeft.Type,
            new Dictionary<string, BladeValue>(StringComparer.Ordinal)
            {
                ["lo"] = BladeValue.U8(1),
                ["hi"] = BladeValue.U8(2),
            });

        Assert.Multiple(() =>
        {
            Assert.That(BladeValue.Bool(true) == BladeValue.Bool(true), Is.True);
            Assert.That(BladeValue.I32(7) == BladeValue.U32(7), Is.True);
            Assert.That(firstArray == secondArray, Is.True);
            Assert.That(firstArray.GetHashCode(), Is.EqualTo(secondArray.GetHashCode()));
            Assert.That(aggregateLeft == aggregateRight, Is.True);
            Assert.That(aggregateLeft.GetHashCode(), Is.EqualTo(aggregateRight.GetHashCode()));
            Assert.That(BladeValue.Void == BladeValue.Void, Is.True);
            Assert.That(BladeValue.Undefined == BladeValue.Undefined, Is.True);
        });
    }

    [Test]
    public void BladeValuePointerEquality_UsesProvenanceBeforeAbsoluteAddressFallback()
    {
        VariableSymbol sameSymbol = IrTestFactory.CreateVariableSymbol("arr", BuiltinTypes.U8, VariableStorageClass.Hub, VariableScopeKind.GlobalStorage);
        VariableSymbol differentSymbol = IrTestFactory.CreateVariableSymbol("other", BuiltinTypes.U8, VariableStorageClass.Hub, VariableScopeKind.GlobalStorage);
        PointerTypeSymbol singlePointer = new(BuiltinTypes.U8, isConst: false, storageClass: VariableStorageClass.Hub);
        MultiPointerTypeSymbol multiPointer = new(BuiltinTypes.U8, isConst: true, storageClass: VariableStorageClass.Hub);
        BladeValue left = BladeValue.Pointer(singlePointer, new PointedValue(sameSymbol, 2));
        BladeValue same = BladeValue.Pointer(multiPointer, new PointedValue(sameSymbol, 2));
        BladeValue differentOffset = BladeValue.Pointer(singlePointer, new PointedValue(sameSymbol, 5));
        BladeValue differentSymbolPointer = BladeValue.Pointer(singlePointer, new PointedValue(differentSymbol, 2));
        BladeValue absoluteHub = BladeValue.Pointer(singlePointer, new PointedValue(new AbsoluteAddressSymbol(12, VariableStorageClass.Hub), 1));
        BladeValue fixedHub = BladeValue.Pointer(multiPointer, new PointedValue(IrTestFactory.CreateVariableSymbol("fixed", BuiltinTypes.U8, VariableStorageClass.Hub, VariableScopeKind.GlobalStorage, fixedAddress: 13), 0));
        BladeValue absoluteReg = BladeValue.Pointer(new PointerTypeSymbol(BuiltinTypes.U8, isConst: false, storageClass: VariableStorageClass.Reg), new PointedValue(new AbsoluteAddressSymbol(13, VariableStorageClass.Reg), 0));

        Assert.Multiple(() =>
        {
            Assert.That(left == same, Is.True);
            Assert.That(left.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(left == differentOffset, Is.False);
            Assert.That(left == differentSymbolPointer, Is.False);
            Assert.That(absoluteHub == fixedHub, Is.True);
            Assert.That(absoluteHub == absoluteReg, Is.False);
        });
    }

    [Test]
    public void BladeValueBitCast_AllowsBoolAndBitRoundTrips()
    {
        EvaluationError boolToBit = BladeValue.TryBitCast(BladeValue.Bool(true), BuiltinTypes.Bit, out BladeValue? bitValue);
        EvaluationError bitToBool = BladeValue.TryBitCast(BladeValue.Bit(0), BuiltinTypes.Bool, out BladeValue? boolValue);
        EvaluationError widthMismatch = BladeValue.TryBitCast(BladeValue.Bool(true), BuiltinTypes.U8, out BladeValue? _);

        Assert.Multiple(() =>
        {
            Assert.That(boolToBit, Is.EqualTo(EvaluationError.None));
            Assert.That(bitValue, Is.EqualTo(BladeValue.Bit(1)));
            Assert.That(bitToBool, Is.EqualTo(EvaluationError.None));
            Assert.That(boolValue, Is.EqualTo(BladeValue.Bool(false)));
            Assert.That(widthMismatch, Is.EqualTo(EvaluationError.TypeMismatch));
        });
    }

    private static BoundModule CreateImportedModule(string alias)
    {
        CompilationUnitSyntax syntax = new([], new Token(TokenKind.EndOfFile, Span, string.Empty));
        BoundFunctionMember constructor = IrTestFactory.CreateConstructor();
        return new BoundModule(
            $"/tmp/{alias}.blade",
            syntax,
            constructor,
            [],
            [constructor],
            new Dictionary<string, Symbol>(StringComparer.Ordinal));
    }

    private static RuntimeTypeSymbol GetBuiltinRuntimeType(BuiltinTypeCase typeCase)
    {
        return typeCase switch
        {
            BuiltinTypeCase.Bit => BuiltinTypes.Bit,
            BuiltinTypeCase.Nit => BuiltinTypes.Nit,
            BuiltinTypeCase.Nib => BuiltinTypes.Nib,
            BuiltinTypeCase.U8 => BuiltinTypes.U8,
            BuiltinTypeCase.I8 => BuiltinTypes.I8,
            BuiltinTypeCase.U16 => BuiltinTypes.U16,
            BuiltinTypeCase.I16 => BuiltinTypes.I16,
            BuiltinTypeCase.U32 => BuiltinTypes.U32,
            BuiltinTypeCase.I32 => BuiltinTypes.I32,
            BuiltinTypeCase.Uint => BuiltinTypes.Uint,
            BuiltinTypeCase.Int => BuiltinTypes.Int,
            _ => throw new System.Diagnostics.UnreachableException(),
        };
    }

    public enum BuiltinTypeCase
    {
        Bit,
        Nit,
        Nib,
        U8,
        I8,
        U16,
        I16,
        U32,
        I32,
        Uint,
        Int,
    }
}
