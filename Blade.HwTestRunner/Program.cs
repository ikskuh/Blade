
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

        Runner runner = Runner.Create(new RunnerConfiguration(
            options.PortName,
            options.Loader,
            Runner.DefaultTimeoutMs,
            options.TurbopropNoVersionCheck));

        Console.WriteLine("Launch runner...");

        FixtureConfig config = new()
        {
            ParameterCount = 0,
        };

        TestRun result;
        try
        {
            result = runner.Execute(binary, config, []);
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (FixtureException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        if (result.Result is null)
        {
            Console.Error.WriteLine(result.Exception!.ToString());
            return 1;
        }

        if (result.Result.Outputs.Count != 1)
        {
            Console.Error.WriteLine($"Expected exactly one output, but received {result.Result.Outputs.Count}.");
            return 1;
        }

        uint exitCode = result.Result.Outputs[0];

        if (expected != exitCode)
        {
            Console.WriteLine("Exit Code: 0x{0:X8}", exitCode);
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
        if (string.IsNullOrWhiteSpace(portName))
            throw new InvalidOperationException("Missing required --port.");

        return new ProgramOptions(
            positional.Count >= 1 ? positional[0] : null,
            positional.Count == 2 ? positional[1] : null,
            portName,
            HardwareLoaderSettings.ResolveLoader(loader),
            HardwareLoaderSettings.ResolveTurbopropNoVersionCheck(turbopropNoVersionCheck));
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: Blade.HwTestRunner --port <port-or-endpoint> [--hw-loader auto|p2aas|loadp2|turboprop] [--hw-turboprop-no-version-check|--hw-turboprop-version-check] <binary> [<expectedOutput>]");
    }
}

internal sealed record ProgramOptions(
    string? Binary,
    string? ExpectedOutput,
    string PortName,
    HardwareLoaderKind Loader,
    bool TurbopropNoVersionCheck);
