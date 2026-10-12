using System;
using Blasto.Core;
using Brutal.Numerics;
using KSA;

namespace Blasto.Simulation;

public sealed class SurfaceProbe
{
    public double MaxRange { get; set; } = 350;
    public bool TryProbe(Celestial body, Double3 origin, Double3 exhaust, double thrust, out SurfaceHit hit)
        => TryProbe(body, origin, exhaust, thrust, out hit, out _);
    public bool TryProbe(Celestial body, Double3 origin, Double3 exhaust, double thrust, out SurfaceHit hit, out string result)
    {
        hit = default;
        result = $"{body.Id}: invalid nozzle position or exhaust direction";
        if (!origin.IsFinite || origin.Length < 1 || !exhaust.IsFinite || exhaust.Length < 1e-12 ||
            !double.IsFinite(thrust) || thrust < 0 || !double.IsFinite(MaxRange) || MaxRange <= 0) return false;
        var radial = origin.Normalized();
        exhaust = exhaust.Normalized();
        result = $"{body.Id}: exhaust points away from terrain";
        if (Double3.Dot(radial, exhaust) > .12) return false;
        // DistanceReference and celestial coordinates are metres in this build.
        double Height(Double3 dir) => body.GetTerrainHeightFromDirCcf(ToNative(dir), accurate: true);
        double altitude = origin.Length - body.MeanRadius - Height(radial);
        result = $"{body.Id}: terrain height is not finite";
        if (!double.IsFinite(altitude)) return false;
        result = $"{body.Id}: {altitude:F2} m above terrain; limit {MaxRange:F0} m";
        if (altitude > MaxRange) return false;
        result = $"{body.Id}: origin is {-altitude:F2} m below terrain (tolerance 0.5 m)";
        if (altitude < -.5) return false;
        Double3 point; double distance;
        if (!SurfaceIntersection.TryRay(origin, exhaust, body.MeanRadius, Height, MaxRange * 2, out point, out distance))
        {
            // Four narrow plume-cone rays let a grazing plume reach sloping ground.
            var cone = Double3.Tangents(exhaust);
            bool found = false;
            foreach (var side in new[] { cone.U, -cone.U, cone.V, -cone.V })
            {
                var coneDirection = (exhaust + side * .12).Normalized();
                if (SurfaceIntersection.TryRay(origin, coneDirection, body.MeanRadius, Height, MaxRange * 2, out point, out distance))
                { exhaust = coneDirection; found = true; break; }
            }
            result = $"{body.Id}: plume misses terrain within {MaxRange * 2:F0} m (height {altitude:F2} m)";
            if (!found) return false;
        }
        radial = point.Normalized();
        double terrainHeight = Height(radial);
        var ocean = body.BodyTemplate.OceanReference;
        result = $"{body.Id}: surface is submerged (terrain {terrainHeight:F2} m; ocean {(ocean is null ? 0 : (double)ocean.Level):F2} m)";
        if (ocean is not null && (double)ocean.Level >= terrainHeight - .1) return false;
        var tangent = Double3.Tangents(radial);
        Double3 Surface(Double3 p) { var dir = p.Normalized(); return dir * (body.MeanRadius + Height(dir)); }
        var du = Surface(point + tangent.U * 1.5) - Surface(point - tangent.U * 1.5);
        var dv = Surface(point + tangent.V * 1.5) - Surface(point - tangent.V * 1.5);
        var normal = Double3.Cross(du, dv).Normalized();
        if (Double3.Dot(normal, radial) < 0) normal = -normal;
        double incidence = Math.Clamp(-Double3.Dot(normal, exhaust), 0, 1);
        result = $"{body.Id}: grazing incidence {incidence:F4}; requires more than 0.05";
        if (!double.IsFinite(incidence) || incidence <= .05) return false;
        var lateral = exhaust + normal * incidence;
        var u = lateral.Length > .08 ? lateral.Normalized() : Double3.Tangents(normal).U;
        var v = Double3.Cross(normal, u).Normalized();
        double baseRadius = Math.Clamp(Math.Sqrt(Math.Max(thrust, 0)) * .012, 1.5, 18);
        double radius = Math.Clamp(baseRadius + distance * .10, 1.5, 70);
        hit = new SurfaceHit(body.Id, radial * (body.MeanRadius + terrainHeight), normal, u, v, distance,
            incidence, radius, Math.Clamp(1 / Math.Sqrt(incidence), 1, 2.5), SurfaceProfiles.Classify(body, radial));
        result = $"{body.Id}: invalid sampled surface geometry";
        if (!hit.IsValid()) { hit = default; return false; }
        result = $"{body.Id}: terrain hit at {distance:F2} m; radius {radius:F2} m; incidence {incidence:F3}; {hit.Profile}";
        return true;
    }
    internal static double3 ToNative(Double3 p) => new(p.X, p.Y, p.Z);
    internal static Double3 FromNative(double3 p) => new(p.X, p.Y, p.Z);
}
