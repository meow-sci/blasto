using System;

namespace Blasto.Core;

/// <summary>First dry terrain crossing of a bounded ray; height delegate returns metres above mean radius.</summary>
public static class SurfaceIntersection
{
    public static bool TryRay(Double3 origin, Double3 direction, double meanRadius,
        Func<Double3, double> height, double maxDistance, out Double3 point, out double distance)
    {
        point = default; distance = 0;
        if (!origin.IsFinite || !direction.IsFinite || direction.Length < .5 ||
            !double.IsFinite(meanRadius) || meanRadius <= 0 || !double.IsFinite(maxDistance) || maxDistance <= 0) return false;
        direction = direction.Normalized();
        double Clearance(double t)
        { var p = origin + direction * t; return p.Length - meanRadius - height(p.Normalized()); }
        double a = 0, fa = Clearance(0);
        if (!double.IsFinite(fa) || fa < -.5 || fa > maxDistance) return false;
        if (fa <= 0) { point = origin; return true; }
        // Small near-surface steps prevent skipping relief at inclined incidence. Far steps stay bounded.
        // A grazing ray can stay within a metre of the surface for hundreds of metres.
        // The former 192-step cap stopped those rays after only 48 m at the minimum step.
        long maxSteps = (long)Math.Min(Math.Ceiling(maxDistance / .25) + 1, int.MaxValue);
        for (long i = 0; i < maxSteps && a < maxDistance; i++)
        {
            double b = Math.Min(maxDistance, a + Math.Clamp(fa * .35, .25, 8));
            double fb = Clearance(b);
            if (!double.IsFinite(fb)) return false;
            if (fb <= 0)
            {
                for (int j = 0; j < 22 && b - a > .002; j++)
                {
                    double mid = (a + b) * .5;
                    double fm = Clearance(mid);
                    if (!double.IsFinite(fm)) return false;
                    if (fm > 0) a = mid; else b = mid;
                }
                distance = (a + b) * .5; point = origin + direction * distance; return true;
            }
            a = b; fa = fb;
        }
        return false;
    }
}
