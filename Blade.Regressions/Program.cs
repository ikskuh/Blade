using System;

namespace Blade.Regressions;

internal static class Program
{
    public static int Main(string[] args)
    {
        RegressionRunOptions options = RegressionCommandLine.Parse(args);
        RegressionRunResult result = RegressionRunner.Run(options);
        Console.Write(RegressionReportFormatter.Format(result));
        return result.Succeeded ? 0 : 1;
    }
}
