using System;
using System.Diagnostics;
using System.IO;
using Blade.Diagnostics;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: blade <file.blade>");
    return 1;
}

string filePath = args[0];

if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"error: file not found: {filePath}");
    return 1;
}

string text = File.ReadAllText(filePath);
SourceText source = new(text, filePath);
DiagnosticBag diagnostics = new();

Stopwatch sw = Stopwatch.StartNew();
Parser parser = Parser.Create(source, diagnostics);
CompilationUnitSyntax unit = parser.ParseCompilationUnit();
BoundProgram boundProgram = Binder.Bind(unit, diagnostics);
sw.Stop();

int tokenCount = parser.TokenCount;

foreach (Diagnostic diag in diagnostics)
{
    SourceLocation loc = source.GetLocation(diag.Span.Start);
    Console.WriteLine($"{loc}: {diag}");
}

Console.WriteLine();
Console.WriteLine(BoundTreeWriter.Write(boundProgram));

Console.WriteLine();
Console.WriteLine($"tokens : {tokenCount}");
Console.WriteLine($"members: {unit.Members.Count}");
Console.WriteLine($"bound-fns: {boundProgram.Functions.Count}");
Console.WriteLine($"errors : {diagnostics.Count}");
Console.WriteLine($"time   : {sw.Elapsed.TotalMilliseconds:F2} ms");

return diagnostics.Count > 0 ? 1 : 0;
