using System;
using System.Diagnostics;
using System.IO;

namespace Blade.HwTestRunner;

internal sealed class TurboPropRunner : Runner
{
    internal TurboPropRunner(RunnerConfiguration configuration)
        : base(configuration)
    {
    }

    protected override P2Transport CreateTransport(byte[] testBinary)
    {
        ArgumentNullException.ThrowIfNull(testBinary);

        byte[] loadableBinary = PadToLongBoundary(testBinary);

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

        Process process = StartProcess(startInfo, "turboprop");
        return new TurboPropTransport(process, loadableBinary, this.TimeoutMs);
    }

    private sealed class TurboPropTransport : P2Transport
    {
        private readonly Process process;
        private readonly byte[] loadableBinary;

        internal TurboPropTransport(Process process, byte[] loadableBinary, int timeoutMs)
            : base(
                "turboprop",
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                process.StandardError.BaseStream,
                timeoutMs,
                true)
        {
            this.process = process;
            this.loadableBinary = loadableBinary;
        }

        internal override void BeforeProtocol()
        {
            this.StandardInput.Write(this.loadableBinary, 0, this.loadableBinary.Length);
            this.StandardInput.Close();
        }

        internal override void AfterProtocol()
        {
            ValidateProcessExit(this.process, this.LoaderName);
        }

        internal override void Cleanup(bool completedSuccessfully)
        {
            KillProcess(this.process);
        }

        public override void Dispose()
        {
            this.process.Dispose();
        }
    }
}
