using System;
using System.Linq;
using Blade.Diagnostics;
using Blade.IR;
using NUnit.Framework;

namespace Blade.Tests;

[TestFixture]
public class LayoutSolutionTests
{
    [Test]
    public void LayoutSolution_SolvesHubAndLutMembers_AndEmissionUsesSolvedAddresses()
    {
        CompilationResult result = CompilerDriver.Compile("""
            layout Shared {
                lut var head: u32 @(0x100) = 1;
                lut var tail: u32 = 2;
                hub var flag: u32 @(0x2000) = 3;
                hub var counter: [2]u16 align(8) = [4, 5];
            }

            cog task main() : Shared {
            }
            """, filePath: "<input>");

        Assert.That(result.Diagnostics, Is.Empty, string.Join(Environment.NewLine, result.Diagnostics));

        Assert.That(result.IrBuildResult, Is.Not.Null);
        IrBuildResult build = result.IrBuildResult!;
        LayoutSolution solution = build.LayoutSolution;

        LayoutSlot head = solution.Slots.Single(slot => slot.Symbol.Name == "head");
        LayoutSlot tail = solution.Slots.Single(slot => slot.Symbol.Name == "tail");
        LayoutSlot flag = solution.Slots.Single(slot => slot.Symbol.Name == "flag");
        LayoutSlot counter = solution.Slots.Single(slot => slot.Symbol.Name == "counter");

        Assert.Multiple(() =>
        {
            Assert.That(head.Address, Is.EqualTo(0x100));
            Assert.That(tail.Address, Is.EqualTo(0));
            Assert.That(flag.Address, Is.EqualTo(0x2000));
            Assert.That(counter.Address, Is.EqualTo(0));
            Assert.That(counter.AlignmentInAddressUnits, Is.EqualTo(8));
            Assert.That(counter.SizeInAddressUnits, Is.EqualTo(4));
        });

        Assert.That(build.AssemblyText, Does.Contain("org $200"));
        Assert.That(build.AssemblyText, Does.Contain("org $300"));
        Assert.That(build.AssemblyText, Does.Contain("orgh"));
        Assert.That(build.AssemblyText, Does.Contain("orgh $2000"));
        Assert.That(build.AssemblyText, Does.Match(@"g_tail\s+LONG\s+2"));
        Assert.That(build.AssemblyText, Does.Match(@"g_head\s+LONG\s+1"));
        Assert.That(build.AssemblyText, Does.Match(@"g_counter\s+WORD\s+0\[2\]"));
        Assert.That(build.AssemblyText, Does.Match(@"g_flag\s+LONG\s+3"));
    }

    [Test]
    public void InvalidLayoutAlignment_ReportsE0280()
    {
        CompilationResult result = CompilerDriver.Compile("""
            layout Broken {
                hub var value: u32 align(3) = 1;
            }

            cog task main() : Broken {
            }
            """, filePath: "<input>");

        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == "E0280"), Is.True);
    }

    [Test]
    public void InvalidLayoutAddress_ReportsE0281()
    {
        CompilationResult result = CompilerDriver.Compile("""
            layout Broken {
                lut var table: [2]u32 @(0x1FF) = [1, 2];
            }

            cog task main() : Broken {
            }
            """, filePath: "<input>");

        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == "E0281"), Is.True);
    }

    [Test]
    public void OverlappingLayoutAddresses_ReportE0282()
    {
        CompilationResult result = CompilerDriver.Compile("""
            layout Broken {
                lut var first: [2]u32 @(0x10) = [1, 2];
                lut var second: u32 @(0x11) = 3;
            }

            cog task main() : Broken {
            }
            """, filePath: "<input>");

        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == "E0282"), Is.True);
    }

    [Test]
    public void ExhaustedLutSpace_ReportsE0283()
    {
        CompilationResult result = CompilerDriver.Compile("""
            layout Full {
                lut var whole: [512]u32 = undefined;
                lut var extra: u32 = 1;
            }

            cog task main() : Full {
            }
            """, filePath: "<input>");

        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == "E0283"), Is.True);
    }
}
