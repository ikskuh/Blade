using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Blade.HwTestRunner;

/// <summary>
/// Reads fixture protocol bytes while keeping a full capture of the consumed output.
/// </summary>
internal sealed class FixtureOutputStream
{
    private readonly Stream stream;
    private readonly MemoryStream capturedData = new();

    public FixtureOutputStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
    }

    public int WaitForByte(byte marker, CancellationToken cancellationToken, string timeoutMessage, string eofMessage)
    {
        while (true)
        {
            byte value = ReadByte(cancellationToken, timeoutMessage, eofMessage);
            if (value == marker)
                return checked((int)this.capturedData.Length - 1);
        }
    }

    public string ReadResultLine(string loaderName, byte firstByte, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaderName, nameof(loaderName));

        using MemoryStream buffer = new();
        buffer.WriteByte(firstByte);

        while (true)
        {
            byte value = ReadByte(
                cancellationToken,
                $"No fixture result arrived after {loaderName} reported completion.",
                $"{loaderName} exited before writing the fixture result.");

            if (value == (byte)'\n')
            {
                string result = Encoding.ASCII.GetString(buffer.ToArray()).Trim();
                if (string.IsNullOrEmpty(result))
                    throw new FixtureException($"Unexpected empty result from {loaderName}.");

                return result;
            }

            buffer.WriteByte(value);
        }
    }

    public byte ReadByte(CancellationToken cancellationToken, string timeoutMessage, string eofMessage)
    {
        byte[] buffer = new byte[1];
        int count;
        try
        {
            count = this.stream.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(timeoutMessage);
        }

        if (count == 0)
            throw new FixtureException(eofMessage);

        byte value = buffer[0];
        this.capturedData.WriteByte(value);
        return value;
    }

    public byte[] ReadToEnd()
    {
        using MemoryStream remaining = new();
        this.stream.CopyTo(remaining);
        byte[] bytes = remaining.ToArray();
        this.capturedData.Write(bytes, 0, bytes.Length);
        return bytes;
    }

    public byte[] GetCapturedData()
    {
        return this.capturedData.ToArray();
    }
}