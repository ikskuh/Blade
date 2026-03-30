using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;


namespace Blade.HwTestRunner;

public sealed class Runner
{
    const byte STX = 0x02;
    const byte ETX = 0x03;

    /// <summary>
    /// 
    /// </summary>
    public string PortName { get; init; } = "";

    /// <summary>
    /// Timeout in millisecond until Blade code must exit.
    /// </summary>
    public int Timeout { get; set; } = 2500;

    public Runner()
    {

    }

    public uint Execute(string file)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("File not found", file);

        var startInfo = new ProcessStartInfo
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
        startInfo.ArgumentList.Add(file);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process!");

        var stdout = process.StandardOutput.BaseStream;

        if (!WaitForByte(stdout, STX, 1000))
        {
            throw new TimeoutException("No response from fixture");
        }
        Console.Error.WriteLine("Blade code launched"); // TODO: Log outside
        if (!WaitForByte(stdout, ETX, Timeout))
        {
            throw new TimeoutException($"Blade code did not exit after {Timeout} ms.");
        }
        Console.Error.WriteLine("Blade exited launched"); // TODO: Log outside

        // Terminate loadp2 by closing stdin:
        process.StandardInput.Close();

        // Wait until loadp2 properly terminated
        process.WaitForExit();

        var suffix_bytes = ReadToEnd(stdout);
        var stderr_bytes = ReadToEnd(process.StandardError.BaseStream);

        try
        {

            var suffix_string = Encoding.ASCII.GetString(suffix_bytes);


            if (process.ExitCode != 0)
                throw new FixtureException($"loadp2 crashed with exit code {process.ExitCode}");

            var results = suffix_string.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (results.Length != 1)
                throw new FixtureException($"Unexpected output: {suffix_string}");

            try
            {
                return Convert.ToUInt32(results[0], 16);
            }
            catch (Exception ex)
            {
                throw new FixtureException($"Unexpected result value '{results[0]}': {ex.Message}", ex);
            }
        }
        catch (Exception)
        {

            Console.Error.WriteLine("stdout: {0}", Convert.ToHexString(suffix_bytes));
            Console.Error.WriteLine("stderr: {0}", Convert.ToHexString(stderr_bytes));
            throw;
        }
    }

    static byte[] ReadToEnd(Stream stream)
    {

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    static bool WaitForByte(Stream stream, byte marker, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();

        var any = false;
        try
        {
            var buffer = new byte[1];
            while (true)
            {
                int cnt = stream.Read(buffer);
                if (cnt == 0)
                    return false; // end of stream
                Debug.Assert(cnt == 1);
                if (buffer[0] == marker)
                    return true; // hit
                Console.Write(Encoding.ASCII.GetString(buffer[0..cnt]));
                any = true;
            }
        }
        finally
        {
            if (any)
            {
                Console.WriteLine();
            }
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