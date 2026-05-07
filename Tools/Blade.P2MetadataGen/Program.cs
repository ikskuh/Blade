using System.Text;
using System.Text.Json;

namespace Blade.P2MetadataGen;

/// <summary>
/// Command-line entry point for generating P2 metadata C# from JSON.
/// </summary>
public static class Program
{
    /// <summary>
    /// Generates the output file for the provided input metadata JSON.
    /// </summary>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length != 2)
        {
            Console.Error.WriteLine("p2-metadata-gen <input-json> <output-cs>");
            return 1;
        }

        try
        {
            MetadataJsonRoot model = LoadModel(args[0]);
            string generated = MetadataCodeGenerator.GenerateSource(model, args[0]);

            string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]));
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            File.WriteAllText(args[1], generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static MetadataJsonRoot LoadModel(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        string json = File.ReadAllText(inputPath, Encoding.UTF8);
        MetadataJsonRoot? model = JsonSerializer.Deserialize<MetadataJsonRoot>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                ReadCommentHandling = JsonCommentHandling.Disallow,
            });

        if (model is null)
            throw new FormatException("Metadata JSON did not deserialize to a document.");

        MetadataCodeGenerator.Validate(model);
        return model;
    }
}
