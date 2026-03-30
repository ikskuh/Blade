using System;
using System.Diagnostics;
using System.Threading;


namespace Blade.HwTestRunner;


public sealed class Runner : IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    public string PortName { get; init; } = "";

    public Runner()
    {

    }

    public void Execute(string file)
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
        Thread.Sleep(1000);

        process.StandardInput.Close();

        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException("Failed to launch propeller");

        Console.WriteLine("stdout: {0}", process.StandardOutput.ReadToEnd());
        Console.WriteLine("stderr: {0}", process.StandardError.ReadToEnd());
    }


    public void Dispose()
    {
    }

}