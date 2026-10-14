using System;
using Blasto.Core;
using Blasto.Persistence;

namespace Blasto.Rendering;

/// <summary>Portable visual tuning and contribution ranking; stored physical marks are never changed.</summary>
internal static class BlastVisualMath
{
    public const int HeaderSize = 48;
    public const int MarkStride = 80;
    public const int MaximumMarks = 64;
    public const int Range = HeaderSize + MarkStride * MaximumMarks;
    public const double ViewRangeMeters = 5000;

    public static double EffectiveRadius(BlastMark mark, BlastSettings settings) => mark.Radius * settings.SizeMultiplier;
    public static double EffectiveStrength(BlastMark mark, BlastSettings settings) => Opacity(mark.Exposure * settings.Visibility);

    // Approximate visible footprint and darkening. This preserves the fixed upload budget while
    // keeping a large, pronounced burn ahead of tiny weak sites that happen to be closer.
    public static double ContributionScore(double radius, double anisotropy, double strength,
        double distanceSquared, BlastSettings settings)
    {
        if (radius <= 0 || strength <= 0) return 0;
        double distance = Math.Sqrt(distanceSquared);
        double angularRadius = distance <= radius ? 1 : radius / distance;
        double coverage = .15 + .85 * settings.CenterStrength + .65 * settings.StreakStrength;
        double opacity = Opacity(strength * coverage * Math.Max(settings.Darkness, 1));
        return angularRadius * angularRadius * anisotropy * opacity;
    }

    // Preserve tiny exposures that would otherwise round away in 1-exp(-x).
    private static double Opacity(double depth) => depth < 1e-5
        ? depth * (1 - depth * (.5 - depth / 6)) : 1 - Math.Exp(-depth);

    public static float CheckedGpuFloat(double value, string name, bool positive = false)
    {
        float packed = (float)value;
        if (!double.IsFinite(value) || value < 0 || (positive && value == 0) ||
            !float.IsFinite(packed) || (value > 0 && packed == 0))
            throw new ArgumentOutOfRangeException(name, value,
                "This value cannot be represented by the terrain GPU's finite 32-bit float format; choose a representable value.");
        return packed;
    }

    public static void ValidateGpuSettings(BlastSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CheckedGpuFloat(settings.SizeMultiplier, nameof(settings.SizeMultiplier), positive: true);
        CheckedGpuFloat(settings.SizeMultiplier * .25, "Minimum effective mark radius", positive: true);
        CheckedGpuFloat(settings.SizeMultiplier * 120, "Maximum effective mark radius", positive: true);
        CheckedGpuFloat(settings.Visibility, nameof(settings.Visibility));
        CheckedGpuFloat(settings.Darkness, nameof(settings.Darkness));
        CheckedGpuFloat(settings.StreakStrength, nameof(settings.StreakStrength));
        CheckedGpuFloat(settings.CenterStrength, nameof(settings.CenterStrength));
        CheckedGpuFloat(settings.EdgeSoftness, nameof(settings.EdgeSoftness), positive: true);
    }
}
