
using System;
using System.Collections.Generic;

namespace Blade.HwTestRunner;

internal static class Program
{
    public static int Main(string[] args)
    {
        ProgramOptions options;
        try
        {
            options = Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 1;
        }

        if (options.Binary is null)
        {
            PrintUsage();
            return 1;
        }

        string binary = options.Binary;
        uint expected = options.ExpectedOutput is null ? 0U : UIntLiteralParser.Parse(options.ExpectedOutput);

        Console.WriteLine("Create runner...");

        Runner runner = new()
        {
            PortName = options.PortName,
            Loader = options.Loader,
            TurbopropNoVersionCheck = options.TurbopropNoVersionCheck,
        };

        Console.WriteLine("Launch runner...");

        FixtureConfig config = new()
        {
            ParameterCount = 0,
        };

        uint exit_code = runner.Execute(binary, config, []);

        if (expected != exit_code)
        {
            Console.WriteLine("Exit Code: 0x{0:X8}", exit_code);
            return 1;
        }

        return 0;
    }

    private static ProgramOptions Parse(string[] args)
    {
        string? portName = null;
        HardwareLoaderKind? loader = null;
        bool? turbopropNoVersionCheck = null;
        List<string> positional = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--port":
                    if (i + 1 >= args.Length)
                        throw new InvalidOperationException("Missing value for --port.");
                    portName = args[++i];
                    break;

                case "--hw-loader":
                    if (i + 1 >= args.Length)
                        throw new InvalidOperationException("Missing value for --hw-loader.");
                    loader = HardwareLoaderSettings.ParseLoaderKind(args[++i]);
                    break;

                case "--hw-turboprop-no-version-check":
                    turbopropNoVersionCheck = true;
                    break;

                case "--hw-turboprop-version-check":
                    turbopropNoVersionCheck = false;
                    break;

                default:
                    positional.Add(arg);
                    break;
            }
        }

        if (positional.Count > 2)
            throw new InvalidOperationException("Too many positional arguments.");

        string resolvedPort = ResolvePortName(portName);
        return new ProgramOptions(
            positional.Count >= 1 ? positional[0] : null,
            positional.Count == 2 ? positional[1] : null,
            resolvedPort,
            HardwareLoaderSettings.ResolveLoader(loader),
            HardwareLoaderSettings.ResolveTurbopropNoVersionCheck(turbopropNoVersionCheck));
    }

    private static string ResolvePortName(string? explicitPortName)
    {
        if (!string.IsNullOrWhiteSpace(explicitPortName))
            return explicitPortName;

        string? environmentPort = Environment.GetEnvironmentVariable("BLADE_TEST_PORT");
        return string.IsNullOrWhiteSpace(environmentPort) ? "/dev/ttyUSB0" : environmentPort;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: Blade.HwTestRunner [--port <port>] [--hw-loader auto|loadp2|turboprop] [--hw-turboprop-no-version-check|--hw-turboprop-version-check] <binary> [<expectedOutput>]");
    }
}

internal sealed record ProgramOptions(
    string? Binary,
    string? ExpectedOutput,
    string PortName,
    HardwareLoaderKind Loader,
    bool TurbopropNoVersionCheck);
