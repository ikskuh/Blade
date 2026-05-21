using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Blade.HwTestRunner;

/// <summary>
/// A parameter that can be passed to a fixture. Allows implicit casts from
/// several types, including `int`, `uint` and `bool`.
/// </summary>
public readonly struct FixtureParameter(uint value)
{
    private readonly uint value = value;

    public FixtureParameter(int value)
        : this(unchecked((uint)value))
    {

    }

    public FixtureParameter(bool value)
        : this(value ? 1 : 0)
    {

    }

    /// <summary>Gets the parameter value as an unsigned 32-bit integer.</summary>
    public uint UInt => this.value;

    /// <summary>Gets the parameter value as a signed 32-bit integer.</summary>
    public int Int => unchecked((int)this.value);
}

/// <summary>
/// Configuration of a hardware fixture.
/// </summary>
public sealed class FixtureConfig
{
    /// <summary>
    /// The maximum number of parameters this fixture supports.
    /// </summary>
    public int ParameterCount { get; set; } = 0;
}

/// <summary>
/// Selects the transport used to upload and run hardware fixtures.
/// </summary>
public enum HardwareLoaderKind
{
    Auto,
    P2AAS,
    Loadp2,
    Turboprop,
}

public static class HardwareLoaderSettings
{
    public const string LoaderEnvironmentVariable = "BLADE_TEST_LOADER";
    public const string TurbopropNoVersionCheckEnvironmentVariable = "BLADE_TEST_TURBOPROP_NO_VERSION_CHECK";

    public static HardwareLoaderKind ResolveLoader(HardwareLoaderKind? explicitLoader)
    {
        if (explicitLoader.HasValue)
            return explicitLoader.Value;

        string? environmentValue = Environment.GetEnvironmentVariable(LoaderEnvironmentVariable);
        return string.IsNullOrWhiteSpace(environmentValue)
            ? HardwareLoaderKind.Auto
            : ParseLoaderKind(environmentValue);
    }

    public static bool ResolveTurbopropNoVersionCheck(bool? explicitValue)
    {
        if (explicitValue.HasValue)
            return explicitValue.Value;

        string? environmentValue = Environment.GetEnvironmentVariable(TurbopropNoVersionCheckEnvironmentVariable);
        return string.IsNullOrWhiteSpace(environmentValue)
            ? false
            : ParseBoolean(TurbopropNoVersionCheckEnvironmentVariable, environmentValue);
    }

    public static HardwareLoaderKind ParseLoaderKind(string value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(value));

        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => HardwareLoaderKind.Auto,
            "p2aas" => HardwareLoaderKind.P2AAS,
            "loadp2" => HardwareLoaderKind.Loadp2,
            "turboprop" => HardwareLoaderKind.Turboprop,
            _ => throw new ArgumentException($"Invalid hardware loader '{value}'. Expected auto, p2aas, loadp2, or turboprop.", nameof(value)),
        };
    }

    public static bool ParseBoolean(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name, nameof(name));
        ArgumentNullException.ThrowIfNull(value, nameof(value));

        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => throw new ArgumentException($"Invalid boolean value '{value}' for {name}. Expected true/false, yes/no, or 1/0.", nameof(value)),
        };
    }
}

/// <summary>Describes how to create a hardware fixture runner.</summary>
public sealed record class RunnerConfiguration
{
    /// <summary>Initializes a runner configuration.</summary>
    public RunnerConfiguration(string portName, HardwareLoaderKind loader, int timeoutMs, bool turbopropNoVersionCheck)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException("Port name or endpoint must be a non-empty string.", nameof(portName));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        this.PortName = portName;
        this.Loader = loader;
        this.TimeoutMs = timeoutMs;
        this.TurbopropNoVersionCheck = turbopropNoVersionCheck;
    }

    /// <summary>Gets the serial port path or websocket endpoint used to reach the hardware target.</summary>
    public string PortName { get; }

    /// <summary>Gets the requested backend selection used by <see cref="Runner.Create(RunnerConfiguration)"/>.</summary>
    public HardwareLoaderKind Loader { get; }

    /// <summary>Gets the total timeout in milliseconds until the fixture protocol must complete.</summary>
    public int TimeoutMs { get; }

    /// <summary>Gets whether turboprop should skip its version check.</summary>
    public bool TurbopropNoVersionCheck { get; }
}

/// <summary>Executes hardware fixtures through one of the supported transport backends.</summary>
public abstract class Runner
{
    private const byte STX = 0x02;
    private const byte ETX = 0x03;
    private const byte EOT = 0x04;
    private const int ExitTimeoutMs = 3000;

    private readonly string portName;
    private readonly int timeoutMs;
    private readonly bool turbopropNoVersionCheck;

    /// <summary>Gets the default total timeout in milliseconds for hardware fixture runs.</summary>
    public const int DefaultTimeoutMs = 3500;

    /// <summary>Initializes the shared runner state for one backend implementation.</summary>
    protected Runner(RunnerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        this.portName = configuration.PortName;
        this.timeoutMs = configuration.TimeoutMs;
        this.turbopropNoVersionCheck = configuration.TurbopropNoVersionCheck;
    }

    /// <summary>Creates a concrete backend-specific runner for the supplied configuration.</summary>
    public static Runner Create(RunnerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        HardwareLoaderKind selectedLoader = ResolveLoader(configuration.PortName, configuration.Loader);
        return selectedLoader switch
        {
            HardwareLoaderKind.P2AAS => new P2AASRunner(configuration),
            HardwareLoaderKind.Loadp2 => new LoadP2Runner(configuration),
            HardwareLoaderKind.Turboprop => new TurboPropRunner(configuration),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Gets the serial port path or websocket endpoint used to reach the hardware target.</summary>
    protected string PortName => this.portName;

    /// <summary>Gets the total timeout in milliseconds until the fixture protocol must complete.</summary>
    protected int TimeoutMs => this.timeoutMs;

    /// <summary>Gets whether turboprop should skip its version check.</summary>
    protected bool TurbopropNoVersionCheck => this.turbopropNoVersionCheck;

    /// <summary>
    /// Executes one fixture binary and returns its captured protocol output.
    /// </summary>
    public TestRun Execute(string file, FixtureConfig config, FixtureParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(file, nameof(file));
        ArgumentNullException.ThrowIfNull(config, nameof(config));
        ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));

        if (!File.Exists(file))
            throw new FileNotFoundException("File not found", file);

        if (parameters.Length > config.ParameterCount)
            throw new ArgumentOutOfRangeException(nameof(parameters));

        byte[] testBinary = File.ReadAllBytes(file);
        uint[] inputs = CaptureInputs(parameters);

        PatchParameters(testBinary, parameters);

        using P2Transport transport = CreateTransport(testBinary);
        return ExecuteTransportRun(transport, inputs);
    }

    /// <summary>Creates the backend-specific transport for one fixture execution.</summary>
    protected abstract P2Transport CreateTransport(byte[] testBinary);

    protected static byte[] PadToLongBoundary(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);

        int remainder = input.Length % 4;
        if (remainder == 0)
            return input;

        byte[] padded = new byte[input.Length + (4 - remainder)];
        Buffer.BlockCopy(input, 0, padded, 0, input.Length);
        return padded;
    }

    protected static Process StartProcess(ProcessStartInfo startInfo, string loaderName)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(loaderName);

        try
        {
            return Process.Start(startInfo) ?? throw new FixtureException($"Failed to start {loaderName}.");
        }
        catch (FixtureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FixtureException($"Failed to start {loaderName}: {ex.Message}", ex);
        }
    }

    protected static void ValidateProcessExit(Process process, string loaderName)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(loaderName);

        bool exitedNaturally = process.WaitForExit(milliseconds: 20);
        if (!exitedNaturally)
            KillProcess(process);

        if (exitedNaturally && process.ExitCode != 0)
            throw new FixtureException($"{loaderName} crashed with exit code {process.ExitCode}");
    }

    protected static bool IsCommandAvailable(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return false;

        string[] directories = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string directory in directories)
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return true;
        }

        return false;
    }

    protected static void KillProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            process.WaitForExit(ExitTimeoutMs);
        }
        catch
        {
        }
    }

    protected static bool IsWebSocketEndpoint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint))
            return false;

        return IsWebSocketScheme(endpoint.Scheme);
    }

    protected static bool IsWebSocketScheme(string scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        return string.Equals(scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase);
    }

    private TestRun ExecuteTransportRun(P2Transport transport, uint[] inputs)
    {
        using CancellationTokenSource cts = new(transport.TimeoutMs);
        CancellationToken cancellationToken = cts.Token;
        bool completedSuccessfully = false;

        FixtureOutputStream output = new(transport.StandardOutput);
        FixtureOutputStream error = new(transport.StandardError);
        ReadOnlySequence<byte> log = ReadOnlySequence<byte>.Empty;
        TestResult result;

        try
        {
            try
            {
                transport.BeforeProtocol();

                log = this.ReadFixtureLog(output, transport.LoaderName, cancellationToken, transport.TimeoutMs);

                result = this.ReadFixtureResult(output, transport.LoaderName, cancellationToken, transport.TimeoutMs);

                transport.AfterProtocol();
                completedSuccessfully = true;
            }
            finally
            {
                transport.Cleanup(completedSuccessfully);

                if (transport.CaptureRemainingOutput)
                {
                    output.ReadToEnd();
                    error.ReadToEnd();
                }
            }
        }
        catch (TimeoutException ex)
        {
            return new TestRun(
                inputs,
                ex,
                TestStatus.TimedOut,
                new ReadOnlySequence<byte>(output.GetCapturedData()),
                new ReadOnlySequence<byte>(error.GetCapturedData()),
                ResolveCapturedLog(output, log));
        }
        catch (FixtureException ex)
        {
            return new TestRun(
                inputs,
                ex,
                TestStatus.Crashed,
                new ReadOnlySequence<byte>(output.GetCapturedData()),
                new ReadOnlySequence<byte>(error.GetCapturedData()),
                ResolveCapturedLog(output, log));
        }
        catch (Exception ex)
        {
            return new TestRun(
                inputs,
                ex,
                TestStatus.UnexpectedError,
                new ReadOnlySequence<byte>(output.GetCapturedData()),
                new ReadOnlySequence<byte>(error.GetCapturedData()),
                ResolveCapturedLog(output, log));
        }

        return new TestRun(
            inputs,
            result,
            new ReadOnlySequence<byte>(output.GetCapturedData()),
            new ReadOnlySequence<byte>(error.GetCapturedData()),
            log);
    }

    private static HardwareLoaderKind ResolveLoader(string portName, HardwareLoaderKind loader)
    {
        if (loader != HardwareLoaderKind.Auto)
            return loader;

        if (IsWebSocketEndpoint(portName))
            return HardwareLoaderKind.P2AAS;

        return IsCommandAvailable("turboprop")
            ? HardwareLoaderKind.Turboprop
            : HardwareLoaderKind.Loadp2;
    }

    private ReadOnlySequence<byte> ReadFixtureLog(FixtureOutputStream output, string loaderName, CancellationToken cancellationToken, int timeoutMs)
    {
        int stxIndex = output.WaitForByte(
            STX,
            cancellationToken,
            $"No response from fixture within the total timeout of {timeoutMs} ms via {loaderName}.",
            $"{loaderName} exited before the fixture responded.");
        int etxIndex = output.WaitForByte(
            ETX,
            cancellationToken,
            $"Blade code did not exit within the total timeout of {timeoutMs} ms via {loaderName}.",
            $"{loaderName} exited before Blade code completed.");

        byte[] captured = output.GetCapturedData();
        int logStart = stxIndex + 1;
        int logLength = etxIndex - logStart;
        if (logLength == 0)
            return ReadOnlySequence<byte>.Empty;
        return new(captured[logStart..etxIndex]);
    }

    private TestResult ReadFixtureResult(FixtureOutputStream output, string loaderName, CancellationToken cancellationToken, int timeoutMs)
    {
        List<uint> outputs = [];
        while (true)
        {
            byte firstByte = output.ReadByte(
                cancellationToken,
                $"Fixture outputs were not complete within the total timeout of {timeoutMs} ms via {loaderName}.",
                $"{loaderName} exited before writing the fixture result.");

            if (firstByte == EOT)
                break;

            string resultText = output.ReadResultLine(loaderName, firstByte, cancellationToken);
            outputs.Add(ParseOutput(resultText, loaderName));
        }

        return new TestResult([.. outputs]);
    }

    private static ReadOnlySequence<byte> ResolveCapturedLog(FixtureOutputStream output, ReadOnlySequence<byte> parsedLog)
    {
        ArgumentNullException.ThrowIfNull(output, nameof(output));

        if (!parsedLog.IsEmpty)
            return parsedLog;

        byte[] captured = output.GetCapturedData();
        int stxIndex = Array.IndexOf(captured, STX);
        if (stxIndex < 0 || stxIndex + 1 >= captured.Length)
            return ReadOnlySequence<byte>.Empty;

        int etxIndex = Array.IndexOf(captured, ETX, stxIndex + 1);
        int logEndExclusive = etxIndex >= 0 ? etxIndex : captured.Length;
        if (logEndExclusive <= stxIndex + 1)
            return ReadOnlySequence<byte>.Empty;

        return new ReadOnlySequence<byte>(captured[(stxIndex + 1)..logEndExclusive]);
    }

    private static uint ParseOutput(string resultText, string loaderName)
    {
        try
        {
            return Convert.ToUInt32(resultText, 16);
        }
        catch (Exception ex)
        {
            throw new FixtureException($"Unexpected result value from {loaderName} '{resultText}': {ex.Message}", ex);
        }
    }

    private static uint[] CaptureInputs(FixtureParameter[] parameters)
    {
        uint[] inputs = new uint[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            inputs[i] = parameters[i].UInt;

        return inputs;
    }

    /// <summary>
    /// Patches `input` such that it contains `parameters` starting at offset 4
    /// </summary>
    private void PatchParameters(byte[] input, FixtureParameter[] parameters)
    {
        int size = (4 * (parameters.Length + 4));
        if (input.Length < size)
            throw new ArgumentException($"Test binary must be at least {size} bytes large!");
        Trace.Assert(BitConverter.IsLittleEndian);
        for (int i = 0; i < parameters.Length; i++)
        {
            byte[] bytes = BitConverter.GetBytes(parameters[i].UInt);
            input[4 * i + 4] = bytes[0];
            input[4 * i + 5] = bytes[1];
            input[4 * i + 6] = bytes[2];
            input[4 * i + 7] = bytes[3];
        }
    }

    /// <summary>Defines the hooks that backend-specific transports expose to the shared protocol execution path.</summary>
    protected abstract class P2Transport : IDisposable
    {
        protected P2Transport(
            string loaderName,
            Stream standardInput,
            Stream standardOutput,
            Stream standardError,
            int timeoutMs,
            bool captureRemainingOutput)
        {
            ArgumentNullException.ThrowIfNull(loaderName);
            ArgumentNullException.ThrowIfNull(standardInput);
            ArgumentNullException.ThrowIfNull(standardOutput);
            ArgumentNullException.ThrowIfNull(standardError);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

            this.LoaderName = loaderName;
            this.StandardInput = standardInput;
            this.StandardOutput = standardOutput;
            this.StandardError = standardError;
            this.TimeoutMs = timeoutMs;
            this.CaptureRemainingOutput = captureRemainingOutput;
        }

        internal string LoaderName { get; }

        internal Stream StandardInput { get; }

        internal Stream StandardOutput { get; }

        internal Stream StandardError { get; }

        internal int TimeoutMs { get; }

        internal bool CaptureRemainingOutput { get; }

        internal virtual void BeforeProtocol()
        {
        }

        internal virtual void AfterProtocol()
        {
        }

        internal virtual void Cleanup(bool completedSuccessfully)
        {
        }

        public virtual void Dispose()
        {
        }
    }
}

[System.Serializable]
public class FixtureException : System.Exception
{
    public FixtureException() { }
    public FixtureException(string message) : base(message) { }
    public FixtureException(string message, System.Exception inner) : base(message, inner) { }
}
