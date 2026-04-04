using System;
using System.IO;
using Blade;
using Blade.IR;
using Blade.IR.Asm;
using Blade.Semantics;

namespace Blade.Tests;

[TestFixture]
public sealed class RuntimeTemplateTests
{
    [Test]
    public void TryLoad_RejectsTemplateMissingConMarker()
    {
        using TempDirectory tempDirectory = new();
        string templatePath = Path.Combine(tempDirectory.Path, "missing_con.spin2");
        File.WriteAllText(templatePath, """
            CON
                ' not the marker

            DAT
              runtime_entry
                JMP #blade_entry
              ' <<BLADE_DAT>>
            """);

        bool succeeded = RuntimeTemplate.TryLoad(templatePath, out RuntimeTemplate? template, out string? errorMessage);

        Assert.That(succeeded, Is.False);
        Assert.That(template, Is.Null);
        Assert.That(errorMessage, Is.EqualTo($"error: runtime template '{templatePath}' must contain exactly one special comment marker for {RuntimeTemplate.ConMarker}."));
    }

    [Test]
    public void TryLoad_RejectsDuplicateDatMarker()
    {
        using TempDirectory tempDirectory = new();
        string templatePath = Path.Combine(tempDirectory.Path, "duplicate_dat.spin2");
        File.WriteAllText(templatePath, """
            CON
                ' <<BLADE_CON>>

            DAT
                ' <<BLADE_DAT>>
                ' <<BLADE_DAT>>

              blade_halt
                JMP #blade_halt
            """);

        bool succeeded = RuntimeTemplate.TryLoad(templatePath, out RuntimeTemplate? template, out string? errorMessage);

        Assert.That(succeeded, Is.False);
        Assert.That(template, Is.Null);
        Assert.That(errorMessage, Is.EqualTo($"error: runtime template '{templatePath}' must contain exactly one special comment marker for {RuntimeTemplate.DatMarker}."));
    }

    [Test]
    public void FinalAssemblyWriter_ComposesTemplateAndKeepsGeneratedSectionsHeaderFree()
    {
        using TempDirectory tempDirectory = new();
        string templatePath = Path.Combine(tempDirectory.Path, "basic.spin2");
        File.WriteAllText(templatePath, """
            CON
                RUNTIME_BOOT_MAGIC = $42
                ' <<BLADE_CON>>

            DAT
              runtime_entry
                JMP #blade_entry
              ' <<BLADE_DAT>>

              blade_halt
                JMP #blade_halt
            """);

        bool loadSucceeded = RuntimeTemplate.TryLoad(templatePath, out RuntimeTemplate? runtimeTemplate, out string? errorMessage);
        Assert.That(loadSucceeded, Is.True, errorMessage);

        StoragePlace ledPort = new(
            IrTestFactory.CreateVariableSymbol(
                "LED_PORT",
                storageClass: VariableStorageClass.Reg,
                scopeKind: VariableScopeKind.GlobalStorage,
                fixedAddress: 0x1FC),
            StoragePlaceKind.FixedRegisterAlias,
            fixedAddress: 0x1FC,
            emittedName: "LED_PORT");
        AsmModule module = new(
            [ledPort],
            [new AsmDataBlock(AsmDataBlockKind.External, [new AsmExternalBindingDefinition(ledPort)])],
            [
                CreateAsmFunction(
                    "$top",
                    isEntryPoint: true,
                    CallingConventionTier.EntryPoint,
                    [
                        new AsmLabelNode("$top_bb0"),
                        new AsmInstructionNode(
                            P2Mnemonic.JMP,
                            [new AsmSymbolOperand(new ControlFlowLabelSymbol(RuntimeTemplate.HaltLabel), AsmSymbolAddressingMode.Immediate)]),
                    ]),
            ]);

        string conSectionContents = FinalAssemblyWriter.WriteConSectionContents(module);
        string datSectionContents = FinalAssemblyWriter.WriteDatSectionContents(module);
        FinalAssembly assembly = FinalAssemblyWriter.Build(module, runtimeTemplate);

        Assert.That(conSectionContents, Does.Not.Contain("CON"));
        Assert.That(conSectionContents, Does.Contain("LED_PORT = $1FC"));
        Assert.That(datSectionContents, Does.Not.Contain("DAT"));
        Assert.That(datSectionContents, Does.Not.Contain("org 0"));
        Assert.That(datSectionContents, Does.Contain("blade_entry"));

        Assert.That(assembly.Text, Does.Contain("RUNTIME_BOOT_MAGIC = $42"));
        Assert.That(assembly.Text, Does.Contain("JMP #blade_entry"));
        Assert.That(assembly.Text, Does.Contain("LED_PORT = $1FC"));
        Assert.That(assembly.Text, Does.Contain("JMP #blade_halt"));
    }

    [Test]
    public void FinalAssemblyWriter_RawOutputPlacesDefaultBladeHaltBeforeConstantFile()
    {
        StoragePlace constantOne = new(
            IrTestFactory.CreateVariableSymbol("c_one", storageClass: VariableStorageClass.Reg, scopeKind: VariableScopeKind.GlobalStorage),
            StoragePlaceKind.AllocatableGlobalRegister,
            fixedAddress: null,
            emittedName: "c_one");
        AsmModule module = new(
            [constantOne],
            [
                new AsmDataBlock(
                    AsmDataBlockKind.Constant,
                    [
                        new AsmAllocatedStorageDefinition(
                            constantOne,
                            VariableStorageClass.Reg,
                            BuiltinTypes.U32,
                            [new AsmImmediateOperand(1L)]),
                    ]),
            ],
            [
                CreateAsmFunction(
                    "$top",
                    isEntryPoint: true,
                    CallingConventionTier.EntryPoint,
                    [
                        new AsmLabelNode("$top_bb0"),
                        new AsmInstructionNode(
                            P2Mnemonic.JMP,
                            [new AsmSymbolOperand(new ControlFlowLabelSymbol(RuntimeTemplate.HaltLabel), AsmSymbolAddressingMode.Immediate)]),
                    ]),
            ]);

        string text = FinalAssemblyWriter.Build(module).Text;

        int haltIndex = text.IndexOf("blade_halt", StringComparison.Ordinal);
        int constantFileIndex = text.IndexOf("' --- constant file ---", StringComparison.Ordinal);
        Assert.That(haltIndex, Is.GreaterThanOrEqualTo(0), text);
        Assert.That(constantFileIndex, Is.GreaterThan(haltIndex), text);
    }
}
