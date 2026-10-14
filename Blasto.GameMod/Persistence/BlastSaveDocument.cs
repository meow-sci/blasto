using Blasto.Core;

using System.Text.Json.Serialization;

namespace Blasto.Persistence;

public sealed record BlastSettings(bool Enabled = true, bool DustEnabled = true, double Intensity = 3)
{
    // Optional properties preserve schema-1 saves and the original three-argument API.
    public double SizeMultiplier { get; init; } = 3;
    public double Visibility { get; init; } = 8;
    public double Darkness { get; init; } = 3;
    public double StreakStrength { get; init; } = 2;
    public double CenterStrength { get; init; } = 1;
    public double EdgeSoftness { get; init; } = 1;
    public double ErosionThreshold { get; init; } = 0;
    public double DustOpacity { get; init; } = 1;
    public double DustSize { get; init; } = 1;
    public double DustSpeed { get; init; } = 1;

    public bool IsValid() =>
        ValidScalar(Intensity) && ValidScalar(SizeMultiplier, positive: true) &&
        // Check the complete legal stored-radius range, including marks created after tuning.
        ValidScalar(SizeMultiplier * .25, positive: true) && ValidScalar(SizeMultiplier * 120, positive: true) &&
        ValidScalar(Visibility) && ValidScalar(Darkness) && ValidScalar(StreakStrength) &&
        ValidScalar(CenterStrength) && ValidScalar(EdgeSoftness, positive: true) &&
        ValidScalar(ErosionThreshold) && ValidScalar(DustOpacity) &&
        ValidScalar(DustSize, positive: true) && ValidScalar(DustSpeed);

    // Rendering uses floats. This rejects overflow and positive values that underflow
    // to zero, without imposing the UI's suggested drag ranges on typed/saved values.
    private static bool ValidScalar(double value, bool positive = false) =>
        double.IsFinite(value) && value >= 0 && value <= float.MaxValue &&
        (!positive || (float)value > 0);
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
