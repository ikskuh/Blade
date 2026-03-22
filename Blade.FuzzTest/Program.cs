using System;
using System.IO;
using SharpFuzz;

namespace Blade.FuzzTest;

public class Program
{
    public static void Main(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var reader = new StreamReader(stream);
            // 
        });
    }
}