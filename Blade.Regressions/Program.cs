using System;

namespace Blade.Regressions;

internal static class Program
{
    public static int Main(string[] args)
    {
        RegressionRunOptions options = RegressionCommandLine.Parse(args);
        RegressionRunResult result = RegressionRunner.Run(options);
        string output = options.Json
            ? RegressionJsonFormatter.Format(result)
            : RegressionReportFormatter.Format(result);
        Console.Write(output);
        return result.Succeeded ? 0 : 1;
    }
}
