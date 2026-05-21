using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.Auto);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result!.Outputs, Is.EqualTo(new[] { 0xCAFEBABEU }));
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

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.Auto);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result!.Outputs, Is.EqualTo(new[] { 0xCAFEBABEU }));
            Assert.That(File.Exists(paths.Loadp2ArgsPath), Is.True);
        });
    }

    [Test]
    public void TurbopropLoader_ReceivesPatchedBinaryOnStandardInput()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallFakeLoaders(temp, includeTurboprop: true, includeLoadp2: false);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        TestRun result = ExecuteFixture(
            temp,
            HardwareLoaderKind.Turboprop,
            parameters: [new FixtureParameter(0xAABBCCDDU)]);

        byte[] stdinBytes = File.ReadAllBytes(paths.TurbopropStdinPath);
        Assert.Multiple(() =>
        {
            Assert.That(result.Inputs, Is.EqualTo(new[] { 0xAABBCCDDU }));
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
    public void AutoLoader_SelectsP2AASWhenEndpointIsWebSocketUrl()
    {
        using TempDirectory temp = new();
        using FakeP2AASServer server = FakeP2AASServer.CreateSuccess();

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.Auto, portName: server.Url);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result!.Outputs, Is.EqualTo(new[] { 0xCAFEBABEU }));
            Assert.That(server.UploadedFrames, Has.Count.EqualTo(1));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(server.UploadedFrames[0].AsSpan(0, sizeof(uint))), Is.EqualTo(32U));
            Assert.That(server.UploadedFrames[0], Has.Length.EqualTo(36));
        });
    }

    [Test]
    public void P2AASLoader_UploadsLengthPrefixedPaddedBinary()
    {
        using TempDirectory temp = new();
        using FakeP2AASServer server = FakeP2AASServer.CreateSuccess();

        TestRun result = ExecuteFixture(
            temp,
            HardwareLoaderKind.P2AAS,
            parameters: [new FixtureParameter(0x01020304U)],
            binaryLength: 30,
            portName: server.Url);

        IReadOnlyList<byte[]> uploadedFrames = server.UploadedFrames;
        Assert.Multiple(() =>
        {
            Assert.That(result.Result!.Outputs, Is.EqualTo(new[] { 0xCAFEBABEU }));
            Assert.That(uploadedFrames, Has.Count.EqualTo(1));
            Assert.That(uploadedFrames[0], Has.Length.EqualTo(36));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(uploadedFrames[0].AsSpan(0, sizeof(uint))), Is.EqualTo(32U));
            Assert.That(uploadedFrames[0][8], Is.EqualTo(0x04));
            Assert.That(uploadedFrames[0][9], Is.EqualTo(0x03));
            Assert.That(uploadedFrames[0][10], Is.EqualTo(0x02));
            Assert.That(uploadedFrames[0][11], Is.EqualTo(0x01));
            Assert.That(uploadedFrames[0][34], Is.EqualTo(0));
            Assert.That(uploadedFrames[0][35], Is.EqualTo(0));
        });
    }

    [Test]
    public void P2AASLoader_RejectsTextFrames()
    {
        using TempDirectory temp = new();
        using FakeP2AASServer server = FakeP2AASServer.CreateTextFrame();

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.P2AAS, portName: server.Url);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TestStatus.Crashed));
            Assert.That(result.Exception, Is.TypeOf<FixtureException>());
            Assert.That(result.Exception!.Message, Does.Contain("text frame"));
        });
    }

    [Test]
    public void P2AASLoader_MapsPolicyViolationToTimeout()
    {
        using TempDirectory temp = new();
        using FakeP2AASServer server = FakeP2AASServer.CreatePolicyViolation();

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.P2AAS, portName: server.Url);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TestStatus.TimedOut));
            Assert.That(result.Exception, Is.TypeOf<TimeoutException>());
            Assert.That(result.Log.ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("partial-log")));
        });
    }

    [Test]
    public void P2AASLoader_TreatsFramesAsContinuousByteStream()
    {
        using TempDirectory temp = new();
        using FakeP2AASServer server = FakeP2AASServer.CreateSplitSuccess();

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.P2AAS, portName: server.Url);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TestStatus.Success));
            Assert.That(result.Result!.Outputs, Is.EqualTo(new[] { 0xCAFEBABEU, 0x2AU }));
            Assert.That(result.Log.IsEmpty, Is.True);
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

        Runner runner = CreateRunner(HardwareLoaderKind.Turboprop, timeoutMs: 50);

        TestRun result = runner.Execute(temp.GetFullPath("fixture.bin"), CreateConfig(), []);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TestStatus.TimedOut));
            Assert.That(result.Exception, Is.TypeOf<TimeoutException>());
            Assert.That(result.Exception!.Message, Does.Contain("turboprop"));
        });
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

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.Turboprop);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TestStatus.Crashed));
            Assert.That(result.Exception, Is.TypeOf<FixtureException>());
            Assert.That(result.Exception!.Message, Does.Contain("turboprop"));
        });
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

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.Turboprop);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TestStatus.Crashed));
            Assert.That(result.Exception, Is.TypeOf<FixtureException>());
            Assert.That(result.Exception!.Message, Does.Contain("turboprop"));
        });
    }

    [Test]
    public void LoaderProtocol_CapturesLogOutputsStdoutAndStderr()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallCustomTurboprop(temp, """
        #!/bin/sh
        /bin/cat >/dev/null
        printf 'stderr-line\n' >&2
        printf '\002trace-data\003CAFEBABE\n0000002A\n\004'
        /bin/sleep 30
        """);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        TestRun result = ExecuteFixture(
            temp,
            HardwareLoaderKind.Turboprop,
            parameters: [new FixtureParameter(0x11223344U), new FixtureParameter(true)]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Inputs, Is.EqualTo(new[] { 0x11223344U, 1U }));
            Assert.That(result.Result!.Outputs, Is.EqualTo(new[] { 0xCAFEBABEU, 0x2AU }));
            Assert.That(result.Log.ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("trace-data")));
            Assert.That(result.StdOut.ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("\u0002trace-data\u0003CAFEBABE\n0000002A\n\u0004")));
            Assert.That(result.StdErr.ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("stderr-line\n")));
        });
    }

    [Test]
    public void LoaderProtocol_CapturesPartialLogWhenResultNeverArrives()
    {
        using TempDirectory temp = new();
        FakeLoaderPaths paths = InstallCustomTurboprop(temp, """
        #!/bin/sh
        /bin/cat >/dev/null
        printf 'stderr-before-timeout\n' >&2
        printf '\002partial-log'
        /bin/sleep 30
        """);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);

        Runner runner = CreateRunner(HardwareLoaderKind.Turboprop, timeoutMs: 50);

        TestRun result = runner.Execute(temp.GetFullPath("fixture.bin"), CreateConfig(), []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TestStatus.TimedOut));
            Assert.That(result.Result, Is.Null);
            Assert.That(result.Log.ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("partial-log")));
            Assert.That(result.StdOut.ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("\u0002partial-log")));
            Assert.That(result.StdErr.ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("stderr-before-timeout\n")));
        });
    }

    [Test]
    public void LoaderProtocol_TerminatesProcessAfterEot()
    {
        using TempDirectory temp = new();
        string markerPath = temp.GetFullPath("completed.txt");
        FakeLoaderPaths paths = InstallCustomTurboprop(temp, """
        #!/bin/sh
        /bin/cat >/dev/null
        printf '\002\003CAFEBABE\n\004'
        /bin/sleep 2
        /usr/bin/touch "$BLADE_HW_TURBOPROP_COMPLETED"
        """);
        using EnvironmentScope environment = CreateLoaderEnvironment(paths);
        environment.Set("BLADE_HW_TURBOPROP_COMPLETED", markerPath);

        TestRun result = ExecuteFixture(temp, HardwareLoaderKind.Turboprop);
        bool completed = SpinWait.SpinUntil(() => File.Exists(markerPath), 250);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result!.Outputs, Is.EqualTo(new[] { 0xCAFEBABEU }));
            Assert.That(completed, Is.False);
        });
    }

    private static TestRun ExecuteFixture(
        TempDirectory temp,
        HardwareLoaderKind loader,
        bool turbopropNoVersionCheck = false,
        FixtureParameter[]? parameters = null,
        int binaryLength = 32,
        string? portName = null,
        int timeoutMs = Runner.DefaultTimeoutMs)
    {
        string fixturePath = temp.GetFullPath("fixture.bin");
        if (!File.Exists(fixturePath))
            File.WriteAllBytes(fixturePath, new byte[binaryLength]);

        Runner runner = CreateRunner(loader, portName, timeoutMs, turbopropNoVersionCheck);
        return runner.Execute(fixturePath, CreateConfig(), parameters ?? []);
    }

    private static Runner CreateRunner(
        HardwareLoaderKind loader,
        string? portName = null,
        int timeoutMs = Runner.DefaultTimeoutMs,
        bool turbopropNoVersionCheck = false)
    {
        return Runner.Create(new RunnerConfiguration(
            portName ?? "/dev/fake-p2",
            loader,
            timeoutMs,
            turbopropNoVersionCheck));
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
            printf '\002\003CAFEBABE\n\004'
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
            printf '\002\003CAFEBABE\n\004'
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

    private sealed class FakeP2AASServer : IDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource cancellationSource = new();
        private readonly Task serverTask;
        private readonly Func<WebSocket, byte[], CancellationToken, Task> sessionHandler;
        private readonly TaskCompletionSource<IReadOnlyList<byte[]>> uploadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocket? activeSocket;
        private Exception? failure;

        private FakeP2AASServer(Func<WebSocket, byte[], CancellationToken, Task> sessionHandler)
        {
            this.sessionHandler = sessionHandler;
            int port = ReservePort();
            this.Url = new UriBuilder("ws", "127.0.0.1", port, "/").Uri.AbsoluteUri;
            this.listener = new HttpListener();
            this.listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            this.listener.Start();
            this.serverTask = Task.Run(RunAsync);
        }

        public string Url { get; }

        public IReadOnlyList<byte[]> UploadedFrames => this.uploadCompletion.Task.GetAwaiter().GetResult();

        public static FakeP2AASServer CreateSuccess()
        {
            return new FakeP2AASServer(async (socket, _, cancellationToken) =>
            {
                await SendBinaryAsync(socket, Encoding.ASCII.GetBytes("\u0002\u0003CAFEBABE\n\u0004"), cancellationToken);
                await CloseSocketOutputAsync(socket, WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
            });
        }

        public static FakeP2AASServer CreateTextFrame()
        {
            return new FakeP2AASServer(async (socket, _, cancellationToken) =>
            {
                await SendTextAsync(socket, "text-output", cancellationToken);
                await CloseSocketOutputAsync(socket, WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
            });
        }

        public static FakeP2AASServer CreatePolicyViolation()
        {
            return new FakeP2AASServer(async (socket, _, cancellationToken) =>
            {
                await SendBinaryAsync(socket, Encoding.ASCII.GetBytes("\u0002partial-log"), cancellationToken);
                await CloseSocketOutputAsync(socket, WebSocketCloseStatus.PolicyViolation, "bridge timeout", cancellationToken);
            });
        }

        public static FakeP2AASServer CreateSplitSuccess()
        {
            return new FakeP2AASServer(async (socket, _, cancellationToken) =>
            {
                await SendBinaryAsync(socket, [0x02], cancellationToken);
                await SendBinaryAsync(socket, [0x03, (byte)'C', (byte)'A'], cancellationToken);
                await SendBinaryAsync(socket, Encoding.ASCII.GetBytes("FEBABE\n0000002A\n"), cancellationToken);
                await SendBinaryAsync(socket, [0x04], cancellationToken);
                await CloseSocketOutputAsync(socket, WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
            });
        }

        public void Dispose()
        {
            this.cancellationSource.Cancel();

            try
            {
                this.activeSocket?.Abort();
            }
            catch
            {
            }

            this.listener.Close();

            try
            {
                this.serverTask.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ShouldIgnoreDuringDispose(ex))
            {
            }

            this.cancellationSource.Dispose();
            if (this.failure is not null)
                throw new InvalidOperationException("Fake P2AAS server failed.", this.failure);
        }

        private async Task RunAsync()
        {
            try
            {
                HttpListenerContext context = await this.listener.GetContextAsync();
                HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
                this.activeSocket = webSocketContext.WebSocket;
                using WebSocket socket = this.activeSocket;
                IReadOnlyList<byte[]> uploadFrames = await ReceiveUploadFramesAsync(socket, this.cancellationSource.Token);
                byte[] payload = ValidateUploadFrames(uploadFrames);
                this.uploadCompletion.TrySetResult(uploadFrames);
                await this.sessionHandler(socket, payload, this.cancellationSource.Token);
            }
            catch (Exception ex) when (ShouldIgnoreDuringDispose(ex))
            {
                this.uploadCompletion.TrySetCanceled(this.cancellationSource.Token);
            }
            catch (Exception ex)
            {
                this.failure = ex;
                this.uploadCompletion.TrySetException(ex);
            }
            finally
            {
                this.activeSocket = null;
            }
        }

        private static int ReservePort()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static bool ShouldIgnoreDuringDispose(Exception ex)
        {
            return ex is HttpListenerException or ObjectDisposedException or OperationCanceledException;
        }

        private static async Task<IReadOnlyList<byte[]>> ReceiveUploadFramesAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            byte[] firstFrame = await ReceiveBinaryMessageAsync(socket, cancellationToken);
            if (firstFrame.Length == sizeof(uint))
            {
                uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(firstFrame);
                byte[] secondFrame = await ReceiveBinaryMessageAsync(socket, cancellationToken);
                if (secondFrame.Length != payloadLength)
                {
                    throw new InvalidOperationException(
                        $"Upload payload frame length {secondFrame.Length} did not match prefix {payloadLength}.");
                }

                return [firstFrame, secondFrame];
            }

            return [firstFrame];
        }

        private static byte[] ValidateUploadFrames(IReadOnlyList<byte[]> uploadFrames)
        {
            if (uploadFrames.Count == 0)
                throw new InvalidOperationException("Upload message was missing.");

            if (uploadFrames.Count == 1)
                return ValidateSingleFrameUpload(uploadFrames[0]);

            if (uploadFrames.Count == 2)
                return ValidateTwoFrameUpload(uploadFrames[0], uploadFrames[1]);

            throw new InvalidOperationException($"Expected one or two upload frames, but received {uploadFrames.Count}.");
        }

        private static byte[] ValidateSingleFrameUpload(byte[] uploadMessage)
        {
            if (uploadMessage.Length < sizeof(uint))
                throw new InvalidOperationException("Upload message was missing the 4-byte payload length prefix.");

            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(uploadMessage.AsSpan(0, sizeof(uint)));
            int actualPayloadLength = uploadMessage.Length - sizeof(uint);
            if (payloadLength == 0)
                throw new InvalidOperationException("Upload payload length must be greater than zero.");

            if (payloadLength > 524288)
                throw new InvalidOperationException("Upload payload length exceeded the P2AAS maximum size.");

            if (payloadLength % 4 != 0)
                throw new InvalidOperationException("Upload payload length must be divisible by four.");

            if (payloadLength != actualPayloadLength)
            {
                throw new InvalidOperationException(
                    $"Upload payload length prefix {payloadLength} did not match actual payload length {actualPayloadLength}.");
            }

            return uploadMessage[sizeof(uint)..];
        }

        private static byte[] ValidateTwoFrameUpload(byte[] lengthFrame, byte[] payloadFrame)
        {
            if (lengthFrame.Length != sizeof(uint))
                throw new InvalidOperationException("The length frame must contain exactly 4 bytes.");

            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthFrame);
            int actualPayloadLength = payloadFrame.Length;
            if (payloadLength == 0)
                throw new InvalidOperationException("Upload payload length must be greater than zero.");

            if (payloadLength > 524288)
                throw new InvalidOperationException("Upload payload length exceeded the P2AAS maximum size.");

            if (payloadLength % 4 != 0)
                throw new InvalidOperationException("Upload payload length must be divisible by four.");

            if (payloadLength != actualPayloadLength)
            {
                throw new InvalidOperationException(
                    $"Upload payload length prefix {payloadLength} did not match actual payload length {actualPayloadLength}.");
            }

            return payloadFrame;
        }

        private static async Task<byte[]> ReceiveBinaryMessageAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            using MemoryStream stream = new();

            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new InvalidOperationException($"Expected a binary upload message, but received {result.MessageType}.");

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return stream.ToArray();
            }
        }

        private static Task SendBinaryAsync(WebSocket socket, byte[] bytes, CancellationToken cancellationToken)
        {
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }

        private static Task SendTextAsync(WebSocket socket, string text, CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        private static Task CloseSocketOutputAsync(WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
        {
            return socket.CloseOutputAsync(status, description, cancellationToken);
        }
    }

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
