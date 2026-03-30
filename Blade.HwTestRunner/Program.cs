
using System;


namespace Blade.HwTestRunner;




internal static class Program
{
    public static int Main(string[] args)
    {
        
        Console.WriteLine("Create runner...");
        using var runner = new Runner()
        {
            PortName = "/dev/ttyUSB0",
        };
        
        Console.WriteLine("Launch runner...");

        runner.Execute("/home/felix/projects/nerdgruppe/blade/Work/TestRunner/envelope.bin");


        return 0;
    }

}

