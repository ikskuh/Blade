using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Blade.HwTestRunner;

/// <summary>
/// Captures the parsed outputs of a successful fixture run.
/// </summary>
public sealed class TestResult
{
    public TestResult(IReadOnlyList<uint> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs, nameof(outputs));

        this.Outputs = Array.AsReadOnly(CopyValues(outputs));
    } 

    /// <summary>
    /// Gets the parsed output values emitted between ETX and EOT.
    /// </summary>
    public IReadOnlyList<uint> Outputs { get; }

    private static uint[] CopyValues(IReadOnlyList<uint> values)
    {
        uint[] copy = new uint[values.Count];
        for (int i = 0; i < values.Count; i++)
            copy[i] = values[i];

        return copy;
    }
}

/// <summary>
/// The short-summary status of a hardware fixture run.
/// </summary>
public enum TestStatus
{
    Success,
    TimedOut,
    Crashed,
    UnexpectedError,
}

/// <summary>
/// Captures the observable result of a hardware fixture run.
/// </summary>
public sealed class TestRun
{
    public TestRun(
        IReadOnlyList<uint> inputs,
        TestResult result,
        ReadOnlySequence<byte> stdOut,
        ReadOnlySequence<byte> stdErr,
        ReadOnlySequence<byte> log)
        : this(inputs, result, null, TestStatus.Success, stdOut, stdErr, log)
    {
    }

    public TestRun(
        IReadOnlyList<uint> inputs,
        Exception exception,
        TestStatus status,
        ReadOnlySequence<byte> stdOut,
        ReadOnlySequence<byte> stdErr,
        ReadOnlySequence<byte> log)
        : this(inputs, null, exception, status, stdOut, stdErr, log)
    {
    }

    private TestRun(
        IReadOnlyList<uint> inputs,
        TestResult? result,
        Exception? exception,
        TestStatus status,
        ReadOnlySequence<byte> stdOut,
        ReadOnlySequence<byte> stdErr,
        ReadOnlySequence<byte> log)
    {
        ArgumentNullException.ThrowIfNull(inputs, nameof(inputs));

        if ((result is null) == (exception is null))
            throw new ArgumentException("Exactly one of result and exception must be non-null.");

        switch (status)
        {
            case TestStatus.Success:
                Debug.Assert(result is not null);
                Debug.Assert(exception is null);
                break;

            case TestStatus.TimedOut:
                Debug.Assert(result is null);
                Debug.Assert(exception is TimeoutException);
                break;

            case TestStatus.Crashed:
                Debug.Assert(result is null);
                Debug.Assert(exception is FixtureException);
                break;

            case TestStatus.UnexpectedError:
                Debug.Assert(result is null);
                Debug.Assert(exception is not null);
                Debug.Assert(exception is not TimeoutException);
                Debug.Assert(exception is not FixtureException);
                break;

            default:
                throw new UnreachableException();
        }

        this.Inputs = Array.AsReadOnly(CopyValues(inputs));
        this.Result = result;
        this.Exception = exception;
        this.Status = status;
        this.StdOut = stdOut;
        this.StdErr = stdErr;
        this.Log = log;
    }

    /// <summary>
    /// Gets the short-summary status of the test run.
    /// </summary>
    public TestStatus Status { get; }

    /// <summary>
    /// Gets the results of a test run.
    /// </summary>
    /// <remarks>If <c>null</c>, the test execution failed and <see cref="Exception"/> is set.</remarks>
    public TestResult? Result { get; }

    /// <summary>
    /// Gets the exception that aborted the test run.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Gets the parameter values that were written into the fixture binary.
    /// </summary>
    public IReadOnlyList<uint> Inputs { get; }

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
