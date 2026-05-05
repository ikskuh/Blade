using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Blade.HwTestRunner;

/// <summary>
/// Captures the observable result of a hardware fixture run.
/// </summary>
public sealed class TestResult
{
    private readonly ReadOnlyCollection<uint> inputs;
    private readonly ReadOnlyCollection<uint> outputs;

    public TestResult(
        IReadOnlyList<uint> inputs,
        IReadOnlyList<uint> outputs,
        ReadOnlySequence<byte> log,
        ReadOnlySequence<byte> stdOut,
        ReadOnlySequence<byte> stdErr)
    {
        ArgumentNullException.ThrowIfNull(inputs, nameof(inputs));
        ArgumentNullException.ThrowIfNull(outputs, nameof(outputs));

        this.inputs = Array.AsReadOnly(CopyValues(inputs));
        this.outputs = Array.AsReadOnly(CopyValues(outputs));
        Log = log;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    /// <summary>
    /// Gets the parameter values that were written into the fixture binary.
    /// </summary>
    public IReadOnlyList<uint> Inputs => this.inputs;

    /// <summary>
    /// Gets the parsed output values emitted between ETX and EOT.
    /// </summary>
    public IReadOnlyList<uint> Outputs => this.outputs;

    /// <summary>
    /// Gets the raw log bytes that were emitted between STX and ETX.
    /// </summary>
    public ReadOnlySequence<byte> Log { get; }

    /// <summary>
    /// Gets the full captured standard output stream.
    /// </summary>
    public ReadOnlySequence<byte> StdOut { get; }

    /// <summary>
    /// Gets the full captured standard error stream.
    /// </summary>
    public ReadOnlySequence<byte> StdErr { get; }

    private static uint[] CopyValues(IReadOnlyList<uint> values)
    {
        uint[] copy = new uint[values.Count];
        for (int i = 0; i < values.Count; i++)
            copy[i] = values[i];

        return copy;
    }
}