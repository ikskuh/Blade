using System;
using System.IO;

namespace Blade.HwTestRunner;

/// <summary>
/// A temporary file which implements IDisposable
/// </summary>
internal sealed class TempFile : IDisposable
{
    public TempFile()
    {
        this.Path = System.IO.Path.GetTempFileName();
    }

    ~TempFile()
    {
        this.Dispose();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        File.Delete(this.Path);
    }

    public void WriteAllBytes(byte[] buffer) => File.WriteAllBytes(this.Path, buffer);

    public byte[] ReadAllBytes() => File.ReadAllBytes(this.Path);

    public string Path { get; }
}