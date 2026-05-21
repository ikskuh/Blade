using System;
using System.Diagnostics;
using System.IO;

namespace Blade.HwTestRunner;

internal sealed class LoadP2Runner : Runner
{
    internal LoadP2Runner(RunnerConfiguration configuration)
        : base(configuration)
    {
    }

    protected override P2Transport CreateTransport(byte[] testBinary)
    {
        ArgumentNullException.ThrowIfNull(testBinary);

        TempFile patchedFile = new();
        patchedFile.WriteAllBytes(testBinary);

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "loadp2",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(this.PortName);
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add(patchedFile.Path);

            Process process = StartProcess(startInfo, "loadp2");
            return new LoadP2Transport(process, patchedFile, this.TimeoutMs);
        }
        catch
        {
            patchedFile.Dispose();
            throw;
        }
    }

    private sealed class LoadP2Transport : P2Transport
    {
        private readonly Process process;
        private readonly TempFile patchedFile;

        internal LoadP2Transport(Process process, TempFile patchedFile, int timeoutMs)
            : base(
                "loadp2",
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                process.StandardError.BaseStream,
                timeoutMs,
                true)
        {
            this.process = process;
            this.patchedFile = patchedFile;
        }

        internal override void AfterProtocol()
        {
            this.StandardInput.Close();
            ValidateProcessExit(this.process, this.LoaderName);
        }

        internal override void Cleanup(bool completedSuccessfully)
        {
            KillProcess(this.process);
        }

        public override void Dispose()
        {
            this.process.Dispose();
            this.patchedFile.Dispose();
        }
    }
}
