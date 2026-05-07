using System.Diagnostics;
using System.Text;
using P2MetadataGenProgram = Blade.P2MetadataGen.Program;

namespace Blade.Tests;

[TestFixture]
public class P2MetadataGenTests
{
    [Test]
    public void Program_GeneratesExtensionBasedMetadataModel()
    {
        using TempDirectory temp = new();

        int exitCode = P2MetadataGenProgram.Main(
        [
            GetMetadataJsonPath(),
            temp.GetFullPath("P2InstructionMetadata.generated.cs"),
        ]);

        string generated = temp.ReadFile("P2InstructionMetadata.generated.cs", Encoding.UTF8);
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(generated, Does.Contain("public sealed record P2InstructionFormInfo("));
        Assert.That(generated, Does.Contain("IReadOnlySet<P2FlagEffect> AllowedFlagEffects"));
        Assert.That(generated, Does.Contain("IReadOnlySet<P2WrittenRegister> WrittenRegisters"));
        Assert.That(generated, Does.Contain("public bool IsControlFlow => IsCall || IsJump || IsBranch || IsReturn;"));
        Assert.That(generated, Does.Contain("private sealed record class P2MnemonicInfo("));
        Assert.That(generated, Does.Contain("public static class P2MnemonicExtensions"));
        Assert.That(generated, Does.Contain("public static class P2ConditionCodeExtensions"));
        Assert.That(generated, Does.Contain("public static class P2OperandAccessExtensions"));
        Assert.That(generated, Does.Contain("public static bool IsReading(this P2OperandAccess access)"));
        Assert.That(generated, Does.Contain("public static P2ConditionCode GetCanonicalName(this P2ConditionCode code)"));
        Assert.That(generated, Does.Contain("public static IReadOnlyCollection<P2InstructionFormInfo> GetInstructionForms(this P2Mnemonic mnemonic, int operandCount)"));
        Assert.That(generated, Does.Not.Contain("public static class P2InstructionMetadata"));
    }

    [Test]
    public void Program_RejectsBrokenCanonicalReference()
    {
        using TempDirectory temp = new();
        temp.WriteFile(
            "invalid.json",
            """
            {
              "conditionCodes": {
                "IF_C": { "isAlias": false, "canonicalName": null },
                "IF_ALIAS": { "isAlias": true, "canonicalName": "IF_MISSING" }
              },
              "modczOperands": {
                "_SET": { "isAlias": false, "canonicalName": null }
              },
              "specialRegisters": {
                "PTRA": { "address": 504, "description": "Pointer A register." }
              },
              "flagEffects": {
                "WC": { "targetFlag": "c", "operator": "set" }
              },
              "mnemonics": {
                "NOP": {
                  "instructionForms": [
                    {
                      "isAlias": false,
                      "summary": "No operation.",
                      "allowedFlagEffects": [],
                      "operands": [],
                      "writtenRegisters": [],
                      "hwStackEffect": "None",
                      "classification": {
                        "isCall": false,
                        "isJump": false,
                        "isBranch": false,
                        "isReturn": false,
                        "hasNoRegisterEffect": true,
                        "isPureRegisterLocal": false
                      }
                    }
                  ]
                }
              }
            }
            """);

        int exitCode = P2MetadataGenProgram.Main(
        [
            temp.GetFullPath("invalid.json"),
            temp.GetFullPath("ignored.cs"),
        ]);

        Assert.That(exitCode, Is.EqualTo(1));
    }

    [Test]
    public void Program_GeneratedSourceCompilesInIsolation()
    {
        using TempDirectory temp = new();

        int exitCode = P2MetadataGenProgram.Main(
        [
            GetMetadataJsonPath(),
            temp.GetFullPath("Generated/P2InstructionMetadata.generated.cs"),
        ]);

        Assert.That(exitCode, Is.EqualTo(0));

        temp.WriteFile(
            "CompileHarness/CompileHarness.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        temp.WriteFile(
            "CompileHarness/P2SpecialRegister.cs",
            """
            namespace Blade;

            public enum P2SpecialRegister
            {
                DIRA,
                DIRB,
                IJMP1,
                IJMP2,
                IJMP3,
                INA,
                INB,
                IRET1,
                IRET2,
                IRET3,
                OUTA,
                OUTB,
                PA,
                PB,
                PTRA,
                PTRB,
            }
            """);

        temp.WriteFile(
            "CompileHarness/Marker.cs",
            """
            namespace Blade;

            public static class Marker
            {
                public static void Touch()
                {
                    _ = P2ConditionCode.IF_C.GetCanonicalName();
                    _ = P2OperandAccess.ReadWrite.IsReading();
                    _ = P2OperandAccess.ReadWrite.IsWriting();
                }
            }
            """);

        File.Copy(
            temp.GetFullPath("Generated/P2InstructionMetadata.generated.cs"),
            temp.GetFullPath("CompileHarness/P2InstructionMetadata.generated.cs"),
            overwrite: true);

        ProcessStartInfo startInfo = new("dotnet", "build")
        {
            WorkingDirectory = temp.GetFullPath("CompileHarness"),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using Process process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.EqualTo(0), stdout + Environment.NewLine + stderr);
    }

    private static string GetMetadataJsonPath()
    {
        return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../Data/P2InstructionMetadata.json"));
    }
}
