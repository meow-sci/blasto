using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blasto.Core;

namespace Blasto.Persistence;

/// <summary>Versioned, integrity-bound sidecar storage, independent of the game/native runtime.</summary>
public static class BlastSaveCodec
{
    public const string FileName = "blasto.json";
    public const string RecoveryFileName = "blasto.recovery.json";
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumMarks = 4096;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static BlastSaveDocument? Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        byte[] bytes = ReadBounded(path);
        var document = Decode(bytes);
        if (!string.Equals(document.UniverseSha256, HashUniverse(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The blast marks belong to a different universe.xml.");
        return document;
    }

    public static BlastSaveDocument Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Blast save exceeds 16 MiB.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        ValidateUniqueProperties(json.RootElement);
        var document = json.RootElement.Deserialize<BlastSaveDocument>(Options)
            ?? throw new InvalidDataException("Blast save is empty.");
        Validate(document);
        return document;
    }

    public static void Write(string directory, BlastSaveDocument document)
    {
        var bound = document with { UniverseSha256 = HashUniverse(directory) };
        Validate(bound);
        AtomicWrite(Path.Combine(directory, FileName), JsonSerializer.SerializeToUtf8Bytes(bound, Options));
    }

    public static void Validate(BlastSaveDocument document)
    {
        if (document.SchemaVersion != 1 || document.PatternVersion != 1)
            throw new InvalidDataException($"Unsupported blast save/pattern version {document.SchemaVersion}/{document.PatternVersion}.");
        if (document.Settings is null || !document.Settings.IsValid())
            throw new InvalidDataException("Invalid blast effect settings.");
        if (document.Marks is null || document.Marks.Length > MaximumMarks)
            throw new InvalidDataException("Invalid blast mark count.");
        if (document.UniverseSha256 is null || document.UniverseSha256.Length != 64 ||
            !document.UniverseSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid native save checksum.");
        var ids = new HashSet<Guid>();
        foreach (var mark in document.Marks)
            if (mark is null || !mark.IsValid() || !ids.Add(mark.Id))
                throw new InvalidDataException("Invalid or duplicate blast mark.");
    }

    public static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Blast save exceeds 16 MiB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Blast save exceeds 16 MiB.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string HashUniverse(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "universe.xml"));
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON property '{property.Name}'.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateUniqueProperties(child);
    }
}
