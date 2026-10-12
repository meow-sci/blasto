using Blasto.Core;

using System.Text.Json.Serialization;

namespace Blasto.Persistence;

public sealed record BlastSettings(bool Enabled = true, bool DustEnabled = true, double Intensity = 1)
{
    public bool IsValid() => double.IsFinite(Intensity) && Intensity is >= .1 and <= 5;
}

/// <summary>Detached save state. GPU resources and flying dust never enter the document.</summary>
public sealed record BlastSaveDocument
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    [JsonRequired] public int PatternVersion { get; init; } = 1;
    public string GameBuild { get; init; } = "2026.10.10.5554";
    [JsonRequired] public string UniverseSha256 { get; init; } = "";
    [JsonRequired] public BlastSettings Settings { get; init; } = new();
    [JsonRequired] public BlastMark[] Marks { get; init; } = [];
}
