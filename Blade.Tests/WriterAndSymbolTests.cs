using Blade;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Reports;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;
using System.Collections.Generic;
using System.Reflection;

namespace Blade.Tests;

[TestFixture]
public class WriterAndSymbolTests
{
    private static readonly TextSpan Span = new(0, 0);

    private static VariableSymbol CreateVariable(string name, AddressSpace? storageClass, VariableScopeKind scopeKind)
    {
        return IrTestFactory.CreateVariableSymbol(name, BuiltinTypes.U32, storageClass, scopeKind);
    }

    [Test]
    public void VariableSymbol_UsesConcreteVariableTypesForStorageSpecificProperties()
    {
        VariableSymbol local = CreateVariable("local", storageClass: null, VariableScopeKind.Local);
        VariableSymbol topLevel = CreateVariable("top", storageClass: null, VariableScopeKind.Local);
        VariableSymbol globalReg = CreateVariable("global_reg", AddressSpace.Cog, VariableScopeKind.GlobalStorage);
        VariableSymbol globalHub = CreateVariable("global_hub", AddressSpace.Hub, VariableScopeKind.GlobalStorage);
        ControlFlowLabelSymbol label = new("bb0");

        Assert.That(local, Is.TypeOf<LocalVariableSymbol>());
        Assert.That(topLevel, Is.TypeOf<LocalVariableSymbol>());
        Assert.That(globalReg, Is.TypeOf<GlobalVariableSymbol>());
        Assert.That(((GlobalVariableSymbol)globalReg).StorageClass, Is.EqualTo(AddressSpace.Cog));
        Assert.That(((GlobalVariableSymbol)globalHub).StorageClass, Is.EqualTo(AddressSpace.Hub));
        Assert.That(((GlobalVariableSymbol)globalReg).ScopeKind, Is.EqualTo(VariableScopeKind.GlobalStorage));
        Assert.That(((GlobalVariableSymbol)globalHub).Alignment, Is.Null);
        Assert.That(label.SymbolType, Is.EqualTo(SymbolType.ControlFlowLabel));
    }

    [Test]
    public void LirIndexOperations_AcceptArrayAndManyPointerShapes()
    {
        ArrayTypeSymbol arrayType = new(BuiltinTypes.U32, 2);
        MultiPointerTypeSymbol manyPointerType = new(BuiltinTypes.U32, isConst: false, AddressSpace.Cog);

        Assert.That(new LirLoadIndexOperation(arrayType, AddressSpace.Cog).IsValidResultType(BuiltinTypes.U32), Is.True);
        Assert.That(new LirLoadIndexOperation(manyPointerType, AddressSpace.Cog).IsValidResultType(BuiltinTypes.U32), Is.True);
        Assert.That(new LirStoreIndexOperation(arrayType, AddressSpace.Cog).IsValidResultType(BuiltinTypes.U32), Is.True);
        Assert.That(new LirStoreIndexOperation(manyPointerType, AddressSpace.Cog).IsValidResultType(BuiltinTypes.U32), Is.True);
    }

    [Test]
    public void AsmCurrentAddressSymbol_ReportsControlFlowLabelKind()
    {
        Type symbolType = typeof(FinalAssemblyWriter).Assembly.GetType("Blade.IR.Asm.AsmCurrentAddressSymbol", throwOnError: true)!;
        PropertyInfo instanceProperty = symbolType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
        object instance = instanceProperty.GetValue(null)!;
        PropertyInfo kindProperty = symbolType.GetProperty("SymbolType", BindingFlags.Public | BindingFlags.Instance)!;

        Assert.That(kindProperty.GetValue(instance), Is.EqualTo(SymbolType.ControlFlowLabel));
    }

    [Test]
    public void ReportSectionCatalog_ReturnsFinalAssemblyWhenAvailable()
    {
        BoundProgram program = IrTestFactory.CreateBoundProgram("/tmp/test.blade");
        MirModule mir = CreateMirModule();
        LirModule lir = CreateLirModule();
        AsmModule asm = CreateAsmModule();
        ImagePlan imagePlan = IrTestFactory.CreateSingleEntryImagePlan(program.EntryPoint);
        ImagePlacement imagePlacement = ImagePlacer.Place(imagePlan);
        LayoutSolution layoutSolution = LayoutSolver.SolveStableLayouts(program, imagePlacement);
        CogResourceLayoutSet cogResourceLayouts = IrTestFactory.CreateEmptyCogResourceLayouts(imagePlan);
        CompilationOutput output = CreateCompilationOutput(
            program,
            new IrBuildResult(imagePlan, imagePlacement, layoutSolution, cogResourceLayouts, mir, mir, lir, lir, asm, asm, "DAT\n"),
            diagnostics: []);

        IReadOnlyList<object> dumps = ReportSectionCatalog.BuildSections(output).Cast<object>().ToList();

        Assert.That(output.Stages.AssemblyText, Is.EqualTo("DAT\n"));
    }

    [Test]
    public void ReportSectionCatalog_IncludesBoundImagePlanAndLayoutSolution()
    {
        BoundProgram program = IrTestFactory.CreateBoundProgram("/tmp/test.blade");
        MirModule mir = CreateMirModule();
        LirModule lir = CreateLirModule();
        AsmModule asm = CreateAsmModule();
        ImagePlan imagePlan = IrTestFactory.CreateSingleEntryImagePlan(program.EntryPoint);
        ImagePlacement imagePlacement = ImagePlacer.Place(imagePlan);
        LayoutSolution layoutSolution = LayoutSolver.SolveStableLayouts(program, imagePlacement);
        CogResourceLayoutSet cogResourceLayouts = IrTestFactory.CreateEmptyCogResourceLayouts(imagePlan);
        CompilationOutput output = CreateCompilationOutput(
            program,
            new IrBuildResult(imagePlan, imagePlacement, layoutSolution, cogResourceLayouts, mir, mir, lir, lir, asm, asm, "DAT\n"),
            diagnostics: []);

        IReadOnlyList<ReportSection> dumps = ReportSectionCatalog.BuildSections(output);

        Assert.That(dumps.Take(3).Select(static dump => dump.Id), Is.EqualTo(new[] { "bound", "images", "layout-solution" }));
        Assert.That(dumps.Take(3).Select(static dump => dump.FileName), Is.EqualTo(new[] { "00_bound.ir", "02_images.ir", "03_layout_solution.ir" }));
        Assert.That(dumps[1].Title, Is.EqualTo("Images"));
        Assert.That(dumps[2].Title, Is.EqualTo("Layout Solution"));
    }

    [Test]
    public void ReportSectionCatalog_AddsMemoryMapWhenLayoutAndCodegenStateExist()
    {
        CompilationResult compilation = CompilerDriver.Compile("""
            layout Shared {
                hub var hub_flag: u8 = 1;
                lut var lut_word: u32 = 7;
            }

            cog task main : Shared { }
            """, "<input>");

        Assert.That(compilation.Diagnostics, Is.Empty);
        IReadOnlyList<ReportSection> dumps = ReportSectionCatalog.BuildSections(compilation);

        ReportSection memoryMap = dumps.Single(static dump => dump.Id == "image-memory-maps");
        Assert.That(memoryMap.FileName, Is.EqualTo("35_image_memory_maps.ir"));
        string memoryMapText = memoryMap.RenderPlainText();
        Assert.That(memoryMapText, Does.Contain("; Image Memory Maps v1"));
        Assert.That(memoryMapText, Does.Contain("shared hub"));
        Assert.That(memoryMapText, Does.Contain("image main entry mode=Cog"));
    }

    [Test]
    public void ReportSectionCatalog_CompressesFreeHubRowsInMemoryMap()
    {
        CompilationResult compilation = CompilerDriver.Compile("""
            layout Shared {
                hub var flag: u32 @(0x2000) = 3;
                hub var counter: [2]u16 align(8) = [4, 5];
            }

            cog task main() : Shared {
            }
            """, "<input>");

        Assert.That(compilation.Diagnostics, Is.Empty);
        IReadOnlyList<ReportSection> dumps = ReportSectionCatalog.BuildSections(compilation);

        string content = dumps.Single(static dump => dump.Id == "image-memory-maps").RenderPlainText();
        string sharedHubSection = content[..content.IndexOf("\nimage ", StringComparison.Ordinal)];
        string imageSection = content[content.IndexOf("\nimage ", StringComparison.Ordinal)..];
        Assert.That(sharedHubSection, Does.Contain("addr    value        allocated"));
        Assert.That(sharedHubSection, Does.Contain("$00000  ?? ?? ?? ??  image main"));
        Assert.That(sharedHubSection, Does.Contain("$007FC  ?? ?? ?? ??  image main"));
        Assert.That(sharedHubSection, Does.Contain("$00800  -- -- -- --  Shared.counter"));
        Assert.That(sharedHubSection, Does.Contain("$00804  -- -- -- --  -"));
        Assert.That(sharedHubSection, Does.Contain("*"));
        Assert.That(sharedHubSection, Does.Contain("$01FFC  -- -- -- --  -"));
        Assert.That(sharedHubSection, Does.Contain("$02000  03 00 00 00  Shared.flag"));
        Assert.That(sharedHubSection, Does.Contain("$80000  -- -- -- --  -"));
        Assert.That(sharedHubSection, Does.Not.Contain("$00004  -- -- -- --  -"));
        Assert.That(imageSection, Does.Contain("cog\naddr  state      init   owner\n$000  allocated  -      code"));
        Assert.That(imageSection, Does.Contain("$00B  free       -      -"));
        Assert.That(imageSection, Does.Contain("$1F0  reserved   -      -\n*\n$1FF  reserved   -      -"));
        Assert.That(imageSection, Does.Contain("lut\naddr  state      init   owner\n$000  free       -      -\n*\n$1FF  free       -      -"));
    }

    private static CompilationOutput CreateCompilationOutput(BoundProgram program, IrBuildResult stages, IReadOnlyList<Blade.Diagnostics.Diagnostic> diagnostics)
    {
        SourceText source = new(string.Empty, "/tmp/test.blade");
        CompilationUnitSyntax syntax = new([], new Token(TokenKind.EndOfFile, Span, string.Empty));
        return new CompilationOutput(source, syntax, program, stages, diagnostics, tokenCount: 0, CompilationStatus.Succeeded, crash: null);
    }

    [Test]
    public void BoundProgram_ExposesRootModuleEntryPointAndPath()
    {
        BoundProgram program = IrTestFactory.CreateBoundProgram("/tmp/test.blade");

        Assert.That(program.ResolvedFilePath, Is.EqualTo("/tmp/test.blade"));
        Assert.That(program.EntryPoint.Name, Is.EqualTo("main"));
        Assert.That(program.Functions.Single(), Is.SameAs(program.EntryPointFunction));
        Assert.That(program.RootModule.Functions.Single(), Is.SameAs(program.EntryPointFunction));
    }

    [Test]
    public void MirLirAndAsmTextWriters_FormatRepresentativeNodes()
    {
        StoragePlace place = IrTestFactory.CreateStoragePlace("mem-slot", emittedName: "g_mem_slot");

        MirModule mir = CreateMirModule(functions: [
            CreateMirFunction("mir_fn", isEntryPoint: true, FunctionKind.Leaf, [BuiltinTypes.U32],
            [
                new MirBlock(MirBlockRef("bb0"), [new MirBlockParameter(MirValue(0), "p", BuiltinTypes.U32)],
                [
                    new MirLoadPlaceInstruction(MirValue(1), BuiltinTypes.U32, place, Span),
                    new MirRepSetupInstruction(MirValue(1), Span),
                ], new MirUnreachableTerminator(Span)),
            ]),
        ]);

        LirModule lir = CreateLirModule(functions: [
            CreateLirFunction("lir_fn", isEntryPoint: false, FunctionKind.Default, [],
            [
                new LirBlock(LirBlockRef("bb0"), [],
                [
                    new LirOpInstruction(new LirMovOperation(), LirRegister(0), BuiltinTypes.U32,
                        [new LirImmediateOperand(BladeValue.U8Array([104, 101, 108, 108, 111]))],
                        hasSideEffects: false, predicate: P2ConditionCode.IF_C, writesC: true, writesZ: true, Span),
                ], new LirUnreachableTerminator(Span)),
            ]),
        ]);

        AsmModule asm = CreateAsmModule(functions:
        [
            CreateAsmFunction("asm_fn", isEntryPoint: false, CallingConventionTier.General,
            [
                new AsmLabelNode("asm_fn_bb0"),
                new AsmCommentNode("test"),
                new AsmInstructionNode(
                    P2Mnemonic.ADD,
                    [AsmRegister(1), new AsmImmediateOperand(5)],
                    condition: P2ConditionCode.IF_C,
                    flagOutput: new AsmFlagOutput(P2FlagEffect.WCZ, new VirtualAsmFlag(), new VirtualAsmFlag())),
                new AsmInstructionNode(
                    P2Mnemonic.BITC,
                    [AsmRegister(1), new AsmImmediateOperand(0)],
                    flagInput: new AsmFlagInput(new VirtualAsmFlag(), null)),
                new AsmInstructionNode(P2Mnemonic.MOV, [AsmRegister(1), new AsmImmediateOperand(1)]),
            ]),
        ]);

        string mirText = MirTextWriter.Write([mir]);
        string lirText = LirTextWriter.Write([lir]);
        string asmText = new ReportSection("asm", "ASM", "asm.ir", writer => AsmTextWriter.Write(writer, [asm])).RenderPlainText();

        Assert.That(mirText, Does.Contain("load.place"));
        Assert.That(mirText, Does.Contain("rep.setup"));
        Assert.That(mirText, Does.Contain("unreachable"));

        Assert.That(lirText, Does.Contain("[if_c] mov"));
        Assert.That(lirText, Does.Contain("flags=CZ"));
        Assert.That(lirText, Does.Contain("[104, 101, 108, 108, 111]:[5]u8"));
        Assert.That(lirText, Does.Contain("unreachable"));

        Assert.That(asmText, Does.Contain("' test"));
        Assert.That(asmText, Does.Contain("IF_C ADD %r0, #5 WCZ=(%f1, %f2)"));
        Assert.That(asmText, Does.Contain("BITC %r0, #0 C=%f3"));
        Assert.That(asmText, Does.Contain("MOV %r0, #1"));
    }

    [Test]
    public void StoragePlace_DerivesPlacementLabelAndSymbolTypeFromOrthogonalMetadata()
    {
        StoragePlace allocatableRegister = IrTestFactory.CreateStoragePlace(
            "global_reg",
            placement: StoragePlacePlacement.Allocatable,
            storageClass: AddressSpace.Cog,
            emittedName: "g_global_reg");
        StoragePlace fixedLutAlias = IrTestFactory.CreateStoragePlace(
            "fixed_lut",
            placement: StoragePlacePlacement.FixedAlias,
            storageClass: AddressSpace.Lut,
            fixedAddress: 12,
            emittedName: "fixed_lut");
        StoragePlace externalHubAlias = IrTestFactory.CreateStoragePlace(
            "external_hub",
            placement: StoragePlacePlacement.ExternalAlias,
            storageClass: AddressSpace.Hub,
            isExtern: true,
            emittedName: "external_hub");

        Assert.That(allocatableRegister.IsAllocatable, Is.True);
        Assert.That(allocatableRegister.IsFixedAlias, Is.False);
        Assert.That(allocatableRegister.IsExternalAlias, Is.False);
        Assert.That(allocatableRegister.EmitsStorageLabel, Is.True);
        Assert.That(allocatableRegister.SymbolType, Is.EqualTo(SymbolType.RegVariable));

        Assert.That(fixedLutAlias.IsFixedAlias, Is.True);
        Assert.That(fixedLutAlias.IsExternalAlias, Is.False);
        Assert.That(fixedLutAlias.EmitsStorageLabel, Is.True);
        Assert.That(fixedLutAlias.SymbolType, Is.EqualTo(SymbolType.LutVariable));

        Assert.That(externalHubAlias.IsFixedAlias, Is.False);
        Assert.That(externalHubAlias.IsExternalAlias, Is.True);
        Assert.That(externalHubAlias.EmitsStorageLabel, Is.False);
        Assert.That(externalHubAlias.SymbolType, Is.EqualTo(SymbolType.HubVariable));
    }

    [Test]
    public void BackendSymbolNaming_IgnoresUnaddressedExternalAliasesForCollisionResolution()
    {
        GlobalVariableSymbol runtimeResultSymbol = (GlobalVariableSymbol)IrTestFactory.CreateVariableSymbol(
            "rt_result",
            storageClass: AddressSpace.Cog,
            scopeKind: VariableScopeKind.GlobalStorage,
            isExtern: true,
            fixedAddress: 0);
        StoragePlace runtimeResult = new(
            runtimeResultSymbol,
            StoragePlacePlacement.FixedAlias,
            emittedName: null);

        GlobalVariableSymbol unresolvedExternResultSymbol = (GlobalVariableSymbol)IrTestFactory.CreateVariableSymbol(
            "rt_result",
            storageClass: AddressSpace.Cog,
            scopeKind: VariableScopeKind.GlobalStorage,
            isExtern: true);
        StoragePlace unresolvedExternResult = new(
            unresolvedExternResultSymbol,
            StoragePlacePlacement.ExternalAlias,
            emittedName: null);

        Type namingType = typeof(StoragePlace).Assembly.GetType("Blade.IR.BackendSymbolNaming", throwOnError: true)!;
        MethodInfo assignStorageNames = namingType.GetMethod(
            "AssignStorageNames",
            BindingFlags.Public | BindingFlags.Static)!;
        assignStorageNames.Invoke(null, [new StoragePlace[] { runtimeResult, unresolvedExternResult }]);

        Assert.That(runtimeResult.EmittedName, Is.EqualTo("rt_result"));
        Assert.That(unresolvedExternResult.EmittedName, Is.EqualTo("rt_result"));
    }

    [Test]
    public void FinalAssemblyWriter_CollapsesStoragePlacesWithSameEmittedName()
    {
        GlobalVariableSymbol runtimeResultSymbol = (GlobalVariableSymbol)IrTestFactory.CreateVariableSymbol(
            "rt_result",
            storageClass: AddressSpace.Cog,
            scopeKind: VariableScopeKind.GlobalStorage,
            isExtern: true,
            fixedAddress: 0);
        StoragePlace runtimeResult = new(
            runtimeResultSymbol,
            StoragePlacePlacement.FixedAlias,
            emittedName: "rt_result");

        GlobalVariableSymbol unresolvedExternResultSymbol = (GlobalVariableSymbol)IrTestFactory.CreateVariableSymbol(
            "rt_result",
            storageClass: AddressSpace.Cog,
            scopeKind: VariableScopeKind.GlobalStorage,
            isExtern: true);
        StoragePlace unresolvedExternResult = new(
            unresolvedExternResultSymbol,
            StoragePlacePlacement.ExternalAlias,
            emittedName: "rt_result");

        Type labelEmitterType = typeof(FinalAssemblyWriter).GetNestedType("LabelNameEmitter", BindingFlags.NonPublic)!;
        object labelEmitter = Activator.CreateInstance(labelEmitterType, nonPublic: true)!;
        MethodInfo getLabelName = labelEmitterType.GetMethod("GetLabelName", BindingFlags.Public | BindingFlags.Instance)!;

        string runtimeLabel = (string)getLabelName.Invoke(labelEmitter, [runtimeResult, null])!;
        string unresolvedExternLabel = (string)getLabelName.Invoke(labelEmitter, [unresolvedExternResult, null])!;

        Assert.That(runtimeLabel, Is.EqualTo("rt_result"));
        Assert.That(unresolvedExternLabel, Is.EqualTo("rt_result"));
    }

    [Test]
    public void FinalAssemblyWriter_DoesNotCollapseAllocatableStoragePlacesWithSameEmittedName()
    {
        GlobalVariableSymbol mainYieldStateSymbol = (GlobalVariableSymbol)IrTestFactory.CreateVariableSymbol(
            "top_yield_state",
            storageClass: AddressSpace.Cog,
            scopeKind: VariableScopeKind.GlobalStorage,
            isExtern: false);
        StoragePlace mainYieldState = new(
            mainYieldStateSymbol,
            StoragePlacePlacement.Allocatable,
            StoragePlaceRegisterRole.Global,
            emittedName: "g_top_yield_state");

        GlobalVariableSymbol spawnedYieldStateSymbol = (GlobalVariableSymbol)IrTestFactory.CreateVariableSymbol(
            "top_yield_state",
            storageClass: AddressSpace.Cog,
            scopeKind: VariableScopeKind.GlobalStorage,
            isExtern: false);
        StoragePlace spawnedYieldState = new(
            spawnedYieldStateSymbol,
            StoragePlacePlacement.Allocatable,
            StoragePlaceRegisterRole.Global,
            emittedName: "g_top_yield_state");

        Type labelEmitterType = typeof(FinalAssemblyWriter).GetNestedType("LabelNameEmitter", BindingFlags.NonPublic)!;
        object labelEmitter = Activator.CreateInstance(labelEmitterType, nonPublic: true)!;
        MethodInfo getLabelName = labelEmitterType.GetMethod("GetLabelName", BindingFlags.Public | BindingFlags.Instance)!;

        string mainLabel = (string)getLabelName.Invoke(labelEmitter, [mainYieldState, null])!;
        string spawnedLabel = (string)getLabelName.Invoke(labelEmitter, [spawnedYieldState, null])!;

        Assert.That(mainLabel, Is.EqualTo("g_top_yield_state"));
        Assert.That(spawnedLabel, Is.EqualTo("g_top_yield_state_2"));
    }
}
