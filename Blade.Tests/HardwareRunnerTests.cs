using System;
using System.Collections.Generic;
using System.IO;
using Blade.HwTestRunner;

namespace Blade.Tests;

[TestFixture]
[NonParallelizable]
public sealed class HardwareRunnerTests
{
    [Test]
    public void AutoLoader_SelectsTurbopropWhenAvailable()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallFakeLoaders(temp, includeTurboprop: true, includeLoadp2: true);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        uint result = ExecuteFixture(temp, HardwareLoaderKind.Auto);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(0xCAFEBABEU));
            Assert.That(File.Exists(paths.TurbopropArgsPath), Is.True);
            Assert.That(File.Exists(paths.Loadp2ArgsPath), Is.False);
        });
    }

    [Test]
    public void AutoLoader_FallsBackToLoadp2WhenTurbopropIsUnavailable()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallFakeLoaders(temp, includeTurboprop: false, includeLoadp2: true);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        uint result = ExecuteFixture(temp, HardwareLoaderKind.Auto);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(0xCAFEBABEU));
            Assert.That(File.Exists(paths.Loadp2ArgsPath), Is.True);
        });
    }

    [Test]
    public void TurbopropLoader_ReceivesPatchedBinaryOnStandardInput()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallFakeLoaders(temp, includeTurboprop: true, includeLoadp2: false);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        _ = ExecuteFixture(
            temp,
            HardwareLoaderKind.Turboprop,
            parameters: [new FixtureParameter(0xAABBCCDDU)]);

        byte[] stdinBytes = File.ReadAllBytes(paths.TurbopropStdinPath);
        Assert.Multiple(() =>
        {
            Assert.That(stdinBytes[4], Is.EqualTo(0xDD));
            Assert.That(stdinBytes[5], Is.EqualTo(0xCC));
            Assert.That(stdinBytes[6], Is.EqualTo(0xBB));
            Assert.That(stdinBytes[7], Is.EqualTo(0xAA));
        });
    }

    [Test]
    public void TurbopropLoader_PadsStandardInputToLongBoundary()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallFakeLoaders(temp, includeTurboprop: true, includeLoadp2: false);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        _ = ExecuteFixture(
            temp,
            HardwareLoaderKind.Turboprop,
            parameters: [new FixtureParameter(0x01020304U)],
            binaryLength: 30);

        byte[] stdinBytes = File.ReadAllBytes(paths.TurbopropStdinPath);
        Assert.Multiple(() =>
        {
            Assert.That(stdinBytes, Has.Length.EqualTo(32));
            Assert.That(stdinBytes[4], Is.EqualTo(0x04));
            Assert.That(stdinBytes[5], Is.EqualTo(0x03));
            Assert.That(stdinBytes[6], Is.EqualTo(0x02));
            Assert.That(stdinBytes[7], Is.EqualTo(0x01));
            Assert.That(stdinBytes[30], Is.EqualTo(0));
            Assert.That(stdinBytes[31], Is.EqualTo(0));
        });
    }

    [Test]
    public void TurbopropLoader_UsesExpectedCommandLine()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallFakeLoaders(temp, includeTurboprop: true, includeLoadp2: false);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        _ = ExecuteFixture(temp, HardwareLoaderKind.Turboprop, turbopropNoVersionCheck: true);

        Assert.That(
            File.ReadAllLines(paths.TurbopropArgsPath),
            Is.EqualTo(new[]
            {
                "--port=/dev/fake-p2",
                "--monitor",
                "--monitor-format=raw",
                "--no-version-check",
                "-",
            }));
    }

    [Test]
    public void TurbopropLoader_OmitsNoVersionCheckByDefault()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallFakeLoaders(temp, includeTurboprop: true, includeLoadp2: false);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        _ = ExecuteFixture(temp, HardwareLoaderKind.Turboprop);

        Assert.That(
            File.ReadAllLines(paths.TurbopropArgsPath),
            Is.EqualTo(new[]
            {
                "--port=/dev/fake-p2",
                "--monitor",
                "--monitor-format=raw",
                "-",
            }));
    }

    [Test]
    public void Loadp2Loader_UsesPatchedTemporaryFileAndExpectedCommandLine()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallFakeLoaders(temp, includeTurboprop: false, includeLoadp2: true);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        _ = ExecuteFixture(
            temp,
            HardwareLoaderKind.Loadp2,
            parameters: [new FixtureParameter(0x11223344U)]);

        string[] arguments = File.ReadAllLines(paths.Loadp2ArgsPath);
        byte[] copiedBinary = File.ReadAllBytes(paths.Loadp2BinaryPath);

        Assert.Multiple(() =>
        {
            Assert.That(arguments[0], Is.EqualTo("-p"));
            Assert.That(arguments[1], Is.EqualTo("/dev/fake-p2"));
            Assert.That(arguments[2], Is.EqualTo("-t"));
            Assert.That(arguments[3], Is.EqualTo("-q"));
            Assert.That(arguments[4], Is.Not.EqualTo(temp.GetFullPath("fixture.bin")));
            Assert.That(copiedBinary[4], Is.EqualTo(0x44));
            Assert.That(copiedBinary[5], Is.EqualTo(0x33));
            Assert.That(copiedBinary[6], Is.EqualTo(0x22));
            Assert.That(copiedBinary[7], Is.EqualTo(0x11));
        });
    }

    [Test]
    public void LoaderProtocol_TimesOutWhenEndMarkerNeverArrives()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallCustomTurboprop(temp, """
        #!/bin/sh
        /bin/cat >/dev/null
        printf '\002'
        /bin/sleep 30
        """);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        Runner runner = CreateRunner(HardwareLoaderKind.Turboprop);
        runner.Timeout = 50;

        TimeoutException ex = Assert.Throws<TimeoutException>(() => runner.Execute(temp.GetFullPath("fixture.bin"), CreateConfig(), []))!;
        Assert.That(ex.Message, Does.Contain("turboprop"));
    }

    [Test]
    public void LoaderProtocol_RejectsMalformedResult()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallCustomTurboprop(temp, """
        #!/bin/sh
        /bin/cat >/dev/null
        printf '\002\003NOTHEX\n'
        """);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        FixtureException ex = Assert.Throws<FixtureException>(() => ExecuteFixture(temp, HardwareLoaderKind.Turboprop))!;
        Assert.That(ex.Message, Does.Contain("turboprop"));
    }

    [Test]
    public void LoaderProtocol_RejectsPrematureEndOfOutput()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallCustomTurboprop(temp, """
        #!/bin/sh
        /bin/cat >/dev/null
        printf '\002\003'
        """);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        FixtureException ex = Assert.Throws<FixtureException>(() => ExecuteFixture(temp, HardwareLoaderKind.Turboprop))!;
        Assert.That(ex.Message, Does.Contain("turboprop"));
    }

    private static uint ExecuteFixture(
        TempDirectory temp,
        HardwareLoaderKind loader,
        bool turbopropNoVersionCheck = false,
        FixtureParameter[]? parameters = null,
        int binaryLength = 32)
    {
        string fixturePath = temp.GetFullPath("fixture.bin");
        if (!File.Exists(fixturePath))
            File.WriteAllBytes(fixturePath, new byte[binaryLength]);

        Runner runner = CreateRunner(loader);
        runner.TurbopropNoVersionCheck = turbopropNoVersionCheck;
        return runner.Execute(fixturePath, CreateConfig(), parameters ?? []);
    }

    private static Runner CreateRunner(HardwareLoaderKind loader)
    {
        return new Runner
        {
            PortName = "/dev/fake-p2",
            Loader = loader,
        };
    }

    private static FixtureConfig CreateConfig()
    {
        return new FixtureConfig
        {
            ParameterCount = 8,
        };
    }

    private static FakeLoaderPaths InstallFakeLoaders(TempDirectory temp, bool includeTurboprop, bool includeLoadp2)
    {
        FakeLoaderPaths paths = CreateFakeLoaderPaths(temp);
        temp.MakeDir("tools");

        if (includeTurboprop)
        {
            WriteExecutable(temp.GetFullPath("tools/turboprop"), """
            #!/bin/sh
            printf '%s\n' "$@" > "$BLADE_HW_TURBOPROP_ARGS"
            /bin/cat > "$BLADE_HW_TURBOPROP_STDIN"
            printf '\002\003CAFEBABE\n'
            /bin/sleep 30
            """);
        }

        if (includeLoadp2)
        {
            WriteExecutable(temp.GetFullPath("tools/loadp2"), """
            #!/bin/sh
            printf '%s\n' "$@" > "$BLADE_HW_LOADP2_ARGS"
            last=''
            for arg do
                last="$arg"
            done
            /bin/cp "$last" "$BLADE_HW_LOADP2_BINARY"
            printf '\002\003CAFEBABE\n'
            /bin/cat >/dev/null
            """);
        }

        return paths;
    }

    private static FakeLoaderPaths InstallCustomTurboprop(TempDirectory temp, string script)
    {
        FakeLoaderPaths paths = CreateFakeLoaderPaths(temp);
        temp.MakeDir("tools");
        temp.WriteFile("fixture.bin", new byte[32]);
        WriteExecutable(temp.GetFullPath("tools/turboprop"), script);
        return paths;
    }

    private static FakeLoaderPaths CreateFakeLoaderPaths(TempDirectory temp)
    {
        return new FakeLoaderPaths(
            temp.GetFullPath("tools"),
            temp.GetFullPath("turboprop.args"),
            temp.GetFullPath("turboprop.stdin"),
            temp.GetFullPath("loadp2.args"),
            temp.GetFullPath("loadp2.bin"));
    }

    private static EnvironmentScope CreateLoaderEnvironment(FakeLoaderPaths paths)
    {
        EnvironmentScope environment = new();
        environment.Set("PATH", paths.ToolsDirectory);
        environment.Set("BLADE_HW_TURBOPROP_ARGS", paths.TurbopropArgsPath);
        environment.Set("BLADE_HW_TURBOPROP_STDIN", paths.TurbopropStdinPath);
        environment.Set("BLADE_HW_LOADP2_ARGS", paths.Loadp2ArgsPath);
        environment.Set("BLADE_HW_LOADP2_BINARY", paths.Loadp2BinaryPath);
        return environment;
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute);
        }
    }

    private sealed record FakeLoaderPaths(
        string ToolsDirectory,
        string TurbopropArgsPath,
        string TurbopropStdinPath,
        string Loadp2ArgsPath,
        string Loadp2BinaryPath);

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> previousValues = [];

        public void Set(string name, string? value)
        {
            if (!previousValues.ContainsKey(name))
                previousValues.Add(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> previousValue in previousValues)
                Environment.SetEnvironmentVariable(previousValue.Key, previousValue.Value);
        }
    }
}
