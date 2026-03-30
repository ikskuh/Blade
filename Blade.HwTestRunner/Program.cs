
using System;
using System.Globalization;


namespace Blade.HwTestRunner;




internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2)
        {
            Console.Error.WriteLine("usage: Blade.HwTestRunner <binary> [<expectedOutput>]");
            return 1;
        }

        var binary = args[0];
        var expected = args.Length >= 2 ? ParseUInt(args[1]) : 0U;

        Console.WriteLine("Create runner...");

        var runner = new Runner()
        {
            PortName = "/dev/ttyUSB0",
        };

        Console.WriteLine("Launch runner...");

        uint exit_code = runner.Execute(binary);

        if (expected != exit_code)
        {
            Console.WriteLine("Exit Code: 0x{0:X8}", exit_code);
            return 1;
        }

        return 0;
    }

    private static uint ParseUInt(string arg)
    {
        if(arg.StartsWith("0x"))
            return Convert.ToUInt32(arg[2..], 16);
        if(arg.StartsWith("0b"))
            return Convert.ToUInt32(arg[2..], 2);
        if(arg.StartsWith("0o"))
            return Convert.ToUInt32(arg[2..], 8);
        return Convert.ToUInt32(arg, 10);
    }
}

