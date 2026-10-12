using System;
using Blasto.Core;
using KSA;

namespace Blasto.Simulation;

internal static class SurfaceProfiles
{
    public static SurfaceKind Classify(Celestial body, Double3 direction)
    {
        if (body.Id.Equals("Luna", StringComparison.OrdinalIgnoreCase)) return SurfaceKind.Lunar;
        if (body.Id.Equals("Mars", StringComparison.OrdinalIgnoreCase)) return SurfaceKind.Martian;
        var materials = body.BodyTemplate.TerrainReference?.BiomeMaterials?.Materials;
        if (materials is not null && materials.Count > 0)
        {
            var ids = body.SampleBiomeIdMap(SurfaceProbe.ToNative(direction));
            var weights = body.SampleBiomeControlMap(SurfaceProbe.ToNative(direction));
            int id = ids.X; float weight = weights.X;
            if (weights.Y > weight) { id = ids.Y; weight = weights.Y; }
            if (weights.Z > weight) { id = ids.Z; weight = weights.Z; }
            if (weights.W > weight) id = ids.W;
            foreach (var material in materials)
            {
                if (material.BiomeId != id) continue;
                string name = material.BiomeName.ToLowerInvariant();
                if (name.Contains("rock") || name.Contains("concrete") || name.Contains("ice") || name.Contains("snow")) return SurfaceKind.Rock;
                if (name.Contains("sand") || name.Contains("soil") || name.Contains("desert") || name.Contains("dust")) return SurfaceKind.Soil;
                break;
            }
        }
        // Appearance metadata is not a soil model: conservative response for unknown/constructed surfaces.
        return body.GetAtmosphereReference() is null ? SurfaceKind.Lunar : SurfaceKind.Rock;
    }
}
