using System;
using System.Text.Json.Serialization;

namespace Blasto.Core;

public enum SurfaceKind { Lunar, Martian, Soil, Rock }

public sealed record BlastMark(Guid Id, string BodyId, Double3 CenterCcf, Double3 TangentU,
    Double3 TangentV, double Radius, double Exposure, double Anisotropy, int Seed,
    SurfaceKind Profile, double CreatedSeconds, double UpdatedSeconds)
{
    [JsonIgnore] public double Strength => 1 - Math.Exp(-Exposure);
    public bool IsValid() => Id != Guid.Empty && !string.IsNullOrWhiteSpace(BodyId) && BodyId.Length <= 256 &&
        CenterCcf.IsFinite && CenterCcf.Length > 1 && TangentU.IsFinite && TangentV.IsFinite &&
        Math.Abs(TangentU.Length - 1) < .01 && Math.Abs(TangentV.Length - 1) < .01 &&
        Math.Abs(Double3.Dot(TangentU, TangentV)) < .01 &&
        double.IsFinite(Radius) && Radius is >= .25 and <= 120 &&
        double.IsFinite(Exposure) && Exposure is >= 0 and <= 100 &&
        double.IsFinite(Anisotropy) && Anisotropy is >= 1 and <= 3 && Enum.IsDefined(Profile) &&
        double.IsFinite(CreatedSeconds) && double.IsFinite(UpdatedSeconds) && UpdatedSeconds >= CreatedSeconds;
}

public readonly record struct SurfaceHit(string BodyId, Double3 CenterCcf, Double3 NormalCcf,
    Double3 TangentU, Double3 TangentV, double Distance, double Incidence, double Radius,
    double Anisotropy, SurfaceKind Profile)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(BodyId) && BodyId.Length <= 256 &&
        CenterCcf.IsFinite && CenterCcf.Length > 1 && NormalCcf.IsFinite && TangentU.IsFinite && TangentV.IsFinite &&
        Math.Abs(NormalCcf.Length - 1) < .01 && Math.Abs(TangentU.Length - 1) < .01 && Math.Abs(TangentV.Length - 1) < .01 &&
        Math.Abs(Double3.Dot(TangentU, TangentV)) < .01 && Math.Abs(Double3.Dot(TangentU, NormalCcf)) < .01 &&
        Math.Abs(Double3.Dot(TangentV, NormalCcf)) < .01 && Double3.Dot(Double3.Cross(TangentU, TangentV), NormalCcf) > .99 &&
        double.IsFinite(Distance) && Distance is >= 0 and <= 10000 &&
        double.IsFinite(Incidence) && Incidence is > .05 and <= 1 &&
        double.IsFinite(Radius) && Radius is >= .25 and <= 120 &&
        double.IsFinite(Anisotropy) && Anisotropy is >= 1 and <= 3 && Enum.IsDefined(Profile);
}
