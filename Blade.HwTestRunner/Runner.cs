using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

    public uint UInt => this.value;
    
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

public enum HardwareLoaderKind
{
    Auto,
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
            "loadp2" => HardwareLoaderKind.Loadp2,
            "turboprop" => HardwareLoaderKind.Turboprop,
            _ => throw new ArgumentException($"Invalid hardware loader '{value}'. Expected auto, loadp2, or turboprop.", nameof(value)),
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

public sealed class Runner
{
    const byte STX = 0x02;
    const byte ETX = 0x03;
    const int StartupTimeoutMs = 1000;
    const int ResultTimeoutMs = 1000;
    const int ExitTimeoutMs = 3000;

    /// <summary>
    /// 
    /// </summary>
    public string PortName { get; init; } = "";

    /// <summary>
    /// Timeout in millisecond until Blade code must exit.
    /// </summary>
    public int Timeout { get; set; } = 2500;

    public HardwareLoaderKind Loader { get; set; } = HardwareLoaderKind.Auto;

    public bool TurbopropNoVersionCheck { get; set; }

    public Runner()
    {

    }

    public uint Execute(string file, FixtureConfig config, FixtureParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(file, nameof(file));
        ArgumentNullException.ThrowIfNull(config, nameof(config));
        ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));

        if (!File.Exists(file))
            throw new FileNotFoundException("File not found", file);
        
        if(parameters.Length > config.ParameterCount)
            throw new ArgumentOutOfRangeException(nameof(parameters));
        
        byte[] testBinary = File.ReadAllBytes(file);

        PatchParameters(testBinary, parameters);

        HardwareLoaderKind selectedLoader = ResolveLoader();
        return selectedLoader switch
        {
            HardwareLoaderKind.Loadp2 => ExecuteLoadp2(testBinary),
            HardwareLoaderKind.Turboprop => ExecuteTurboprop(testBinary),
            _ => throw new UnreachableException(),
        };
    }

    private uint ExecuteLoadp2(byte[] testBinary)
    {
        using TempFile patchedFile = new();
        patchedFile.WriteAllBytes(testBinary);

        ProcessStartInfo startInfo = new()
        {
            FileName = "loadp2",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-p"); // port name
        startInfo.ArgumentList.Add(this.PortName);
        startInfo.ArgumentList.Add("-t"); // keep terminal connection open
        startInfo.ArgumentList.Add("-q"); // quiet mode
        startInfo.ArgumentList.Add(patchedFile.Path);

        using Process process = StartProcess(startInfo, "loadp2");
        try
        {
            uint result = ReadFixtureResult(process.StandardOutput.BaseStream, "loadp2");

            CloseStandardInput(process);
            if (!process.WaitForExit(ExitTimeoutMs))
                KillProcess(process);

            if (process.ExitCode != 0)
                throw new FixtureException($"loadp2 crashed with exit code {process.ExitCode}");

            return result;
        }
        catch (Exception)
        {
            KillProcess(process);
            DumpStderr(process);
            throw;
        }
    }

    private uint ExecuteTurboprop(byte[] testBinary)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "turboprop",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add($"--port={this.PortName}");
        startInfo.ArgumentList.Add("--monitor");
        startInfo.ArgumentList.Add("--monitor-format=raw");
        if (this.TurbopropNoVersionCheck)
            startInfo.ArgumentList.Add("--no-version-check");
        startInfo.ArgumentList.Add("-");

        using Process process = StartProcess(startInfo, "turboprop");
        try
        {
            byte[] loadableBinary = PadToLongBoundary(testBinary);
            process.StandardInput.BaseStream.Write(loadableBinary, 0, loadableBinary.Length);
            process.StandardInput.Close();

            uint result = ReadFixtureResult(process.StandardOutput.BaseStream, "turboprop");
            KillProcess(process);
            return result;
        }
        catch (Exception)
        {
            KillProcess(process);
            DumpStderr(process);
            throw;
        }
    }

    private static byte[] PadToLongBoundary(byte[] input)
    {
        int remainder = input.Length % 4;
        if (remainder == 0)
            return input;

        byte[] padded = new byte[input.Length + (4 - remainder)];
        Buffer.BlockCopy(input, 0, padded, 0, input.Length);
        return padded;
    }

    private HardwareLoaderKind ResolveLoader()
    {
        if (this.Loader != HardwareLoaderKind.Auto)
            return this.Loader;

        return IsCommandAvailable("turboprop")
            ? HardwareLoaderKind.Turboprop
            : HardwareLoaderKind.Loadp2;
    }

    private static bool IsCommandAvailable(string fileName)
    {
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

    private static Process StartProcess(ProcessStartInfo startInfo, string loaderName)
    {
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

    private uint ReadFixtureResult(Stream stdout, string loaderName)
    {
        WaitForByte(
            stdout,
            STX,
            StartupTimeoutMs,
            $"No response from fixture via {loaderName}.",
            $"{loaderName} exited before the fixture responded.");
        WaitForByte(
            stdout,
            ETX,
            Timeout,
            $"Blade code did not exit after {Timeout} ms via {loaderName}.",
            $"{loaderName} exited before Blade code completed.");

        string resultText = ReadResultLine(stdout, loaderName);
        try
        {
            return Convert.ToUInt32(resultText, 16);
        }
        catch (Exception ex)
        {
            throw new FixtureException($"Unexpected result value from {loaderName} '{resultText}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Patches `input` such that it contains `parameters` starting at offset 4
    /// </summary>
    private void PatchParameters(byte[] input, FixtureParameter[] parameters)
    {
        int size = (4 * (parameters.Length + 4));
        if(input.Length < size)
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

    static byte[] ReadToEnd(Stream stream)
    {

        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    static void WaitForByte(Stream stream, byte marker, int timeoutMs, string timeoutMessage, string eofMessage)
    {
        using CancellationTokenSource cts = new(timeoutMs);
        bool any = false;
        try
        {
            while (true)
            {
                byte value = ReadByte(stream, cts.Token, timeoutMessage, eofMessage);
                if (value == marker)
                    return; // found the marker
                Console.Write(Encoding.ASCII.GetString([value]));
                any = true;
            }
        }
        finally
        {
            if (any)
                Console.WriteLine();
        }
    }

    static string ReadResultLine(Stream stream, string loaderName)
    {
        using CancellationTokenSource cts = new(ResultTimeoutMs);
        using MemoryStream buffer = new();
        while (true)
        {
            byte value = ReadByte(
                stream,
                cts.Token,
                $"No fixture result arrived after {loaderName} reported completion.",
                $"{loaderName} exited before writing the fixture result.");

            if (value == (byte)'\n')
            {
                string result = Encoding.ASCII.GetString(buffer.ToArray()).Trim();
                if (string.IsNullOrEmpty(result))
                    throw new FixtureException($"Unexpected empty result from {loaderName}.");
                return result;
            }

            buffer.WriteByte(value);
        }
    }

    static byte ReadByte(Stream stream, CancellationToken cancellationToken, string timeoutMessage, string eofMessage)
    {
        byte[] buffer = new byte[1];
        int count;
        try
        {
            count = stream.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(timeoutMessage);
        }

        if (count == 0)
            throw new FixtureException(eofMessage);

        Debug.Assert(count == 1);
        return buffer[0];
    }

    static void CloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
        }
    }

    static void KillProcess(Process process)
    {
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

    static void DumpStderr(Process process)
    {
        try
        {
            byte[] stderrBytes = ReadToEnd(process.StandardError.BaseStream);
            if (stderrBytes.Length > 0)
                Console.Error.WriteLine("stderr: {0}", Convert.ToHexString(stderrBytes));
        }
        catch
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
