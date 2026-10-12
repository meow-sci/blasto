using System;
using System.Text.Json.Serialization;

namespace Blasto.Core;

public readonly record struct Double3(double X, double Y, double Z)
{
    [JsonIgnore] public double LengthSquared => X * X + Y * Y + Z * Z;
    [JsonIgnore] public double Length => Math.Sqrt(LengthSquared);
    [JsonIgnore] public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public Double3 Normalized() => Length > 1e-12 ? this / Length : default;
    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Double3 operator -(Double3 a) => a * -1;
    public static Double3 operator *(Double3 a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public static Double3 operator /(Double3 a, double b) => a * (1 / b);
    public static double Dot(Double3 a, Double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Double3 Cross(Double3 a, Double3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static (Double3 U, Double3 V) Tangents(Double3 normal)
    {
        normal = normal.Normalized();
        var reference = Math.Abs(normal.Z) < .9 ? new Double3(0, 0, 1) : new Double3(0, 1, 0);
        var u = Cross(reference, normal).Normalized();
        return (u, Cross(normal, u).Normalized());
    }
}
