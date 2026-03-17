using System;
using System.IO;
using System.Text;

namespace Blade.Tests;

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"blade-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }

    public void WriteFile(string path, string content)
    {
        WriteFile(path, content, Encoding.UTF8);
    }

    public void WriteFile(string path, string content, Encoding encoding)
    {
        string fullPath = Resolve(path);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, content, encoding);
    }

    public void WriteFile(string path, ReadOnlySpan<byte> content)
    {
        string fullPath = Resolve(path);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(fullPath, content.ToArray());
    }

    public byte[] ReadFile(string path)
    {
        return File.ReadAllBytes(Resolve(path));
    }

    public string ReadFile(string path, Encoding encoding)
    {
        return File.ReadAllText(Resolve(path), encoding);
    }

    public void MakeDir(string path)
    {
        Directory.CreateDirectory(Resolve(path));
    }

    public void DeleteFile(string path)
    {
        string fullPath = Resolve(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    public void DeleteDir(string path)
    {
        string fullPath = Resolve(path);
        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: false);
    }

    public void DeleteTree(string path)
    {
        string fullPath = Resolve(path);
        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
    }

    public string GetFullPath(string path)
    {
        return Resolve(path);
    }

    private string Resolve(string relativePath)
    {
        if (System.IO.Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Expected a relative path.");

        string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        string root = System.IO.Path.GetFullPath(Path);
        string separator = System.IO.Path.DirectorySeparatorChar.ToString();
        string rootWithSeparator = root.EndsWith(separator, StringComparison.Ordinal)
            ? root
            : root + separator;
        if (!string.Equals(fullPath, root, StringComparison.Ordinal)
            && !fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path escapes temporary directory root.");
        }

        return fullPath;
    }
}
