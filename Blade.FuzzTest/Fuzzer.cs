using System;
using System.IO;
using System.Text;
using SharpFuzz;

namespace Blade.FuzzTest;

public static class Program
{
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string sourceText = reader.ReadToEnd();
            _ = CompilerDriver.Compile(sourceText, "sharpfuzz-input.blade");
        });

        return 0;
    }
}
