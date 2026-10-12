using System;
using System.Collections.Generic;
using System.Linq;

namespace Blasto.Core;

/// <summary>Detached, bounded history. New sites are explicitly rejected at capacity; existing sites never evict.</summary>
public sealed class BlastSiteStore
{
    public const double DiagnosticRadius = 20;
    public const double DiagnosticExposure = 3;
    private const double CellSize = 64;
    private readonly Dictionary<Guid, BlastMark> _marks = new();
    private readonly Dictionary<(string, long, long, long), List<Guid>> _index = new();
    public int Capacity { get; }
    public int Count => _marks.Count;
    public long Version { get; private set; }
    public long CapacityRejected { get; private set; }
    public BlastSiteStore(int capacity = 4096)
    {
        if (capacity is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }
    public BlastMark[] Snapshot() => _marks.Values.OrderBy(m => m.CreatedSeconds).ThenBy(m => m.Id).ToArray();
    public void Clear() { _marks.Clear(); _index.Clear(); CapacityRejected = 0; Version++; }
    public void Replace(IEnumerable<BlastMark> marks)
    {
        var detached = marks.ToArray();
        if (detached.Length > Capacity) throw new ArgumentException($"Save contains {detached.Length} sites; capacity is {Capacity}.");
        var ids = new HashSet<Guid>();
        foreach (var mark in detached)
            if (mark is null || !mark.IsValid() || !ids.Add(mark.Id)) throw new ArgumentException("Invalid or duplicate blast mark.");
        Clear();
        foreach (var mark in detached) Add(mark);
        Version++;
    }
    public BlastMark? Deposit(SurfaceHit hit, double thrust, double dt, double epoch)
    {
        if (!hit.IsValid() || !double.IsFinite(thrust) || !double.IsFinite(dt) ||
            !double.IsFinite(epoch) || thrust <= 0 || dt <= 0 || dt > 1 || hit.Incidence <= .05 ||
            !double.IsFinite(hit.Radius) || hit.Radius is < .25 or > 120) return null;
        double response = hit.Profile switch { SurfaceKind.Martian => 1.35, SurfaceKind.Soil => .65, SurfaceKind.Rock => .08, _ => 1 };
        double pressure = thrust * hit.Incidence / (Math.PI * hit.Radius * hit.Radius);
        double dose = response * Math.Max(pressure - 15, 0) * dt / 1800;
        if (dose <= 1e-7) return null;
        return DepositExposure(hit, dose, epoch);
    }
    /// <summary>A visible rendering check, independent of physical erosion thresholds and surface response.</summary>
    public BlastMark? DepositDiagnostic(SurfaceHit hit, double epoch)
    {
        if (!hit.IsValid() || !double.IsFinite(epoch)) return null;
        return DepositExposure(hit with { Radius = DiagnosticRadius }, DiagnosticExposure, epoch);
    }
    private BlastMark? DepositExposure(SurfaceHit hit, double dose, double epoch)
    {
        BlastMark? nearest = null;
        double best = Math.Pow(Math.Clamp(hit.Radius * .25, 1, 12), 2);
        var cell = Cell(hit.BodyId, hit.CenterCcf);
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            if (_index.TryGetValue((cell.Item1, cell.Item2 + x, cell.Item3 + y, cell.Item4 + z), out var ids))
                foreach (var id in ids)
                {
                    var mark = _marks[id];
                    double d = (mark.CenterCcf - hit.CenterCcf).LengthSquared;
                    if (mark.Profile == hit.Profile && Math.Max(mark.Radius, hit.Radius) / Math.Min(mark.Radius, hit.Radius) <= 1.35 && d < best && Double3.Dot(Double3.Cross(mark.TangentU, mark.TangentV), hit.NormalCcf) > .9)
                    { best = d; nearest = mark; }
                }
        if (nearest is not null)
        {
            // Fixed orientation and seed keep streaks still across repeated burns and save/load.
            double radius = Math.Max(nearest.Radius, Math.Min(120, hit.Radius + Math.Sqrt(best)));
            var updated = nearest with { Radius = radius, Exposure = Math.Min(100, nearest.Exposure + dose), UpdatedSeconds = Math.Max(epoch, nearest.UpdatedSeconds) };
            _marks[nearest.Id] = updated; Version++; return updated;
        }
        if (Count >= Capacity) { CapacityRejected++; return null; }
        var created = new BlastMark(Guid.NewGuid(), hit.BodyId, hit.CenterCcf, hit.TangentU, hit.TangentV,
            hit.Radius, Math.Min(100, dose), Math.Clamp(hit.Anisotropy, 1, 3), StableSeed(hit.BodyId, hit.CenterCcf), hit.Profile, epoch, epoch);
        if (!created.IsValid()) return null;
        Add(created); Version++; return created;
    }
    private void Add(BlastMark mark)
    {
        _marks.Add(mark.Id, mark);
        var cell = Cell(mark.BodyId, mark.CenterCcf);
        if (!_index.TryGetValue(cell, out var ids)) _index[cell] = ids = new List<Guid>();
        ids.Add(mark.Id);
    }
    private static (string, long, long, long) Cell(string body, Double3 p) =>
        (body, (long)Math.Floor(p.X / CellSize), (long)Math.Floor(p.Y / CellSize), (long)Math.Floor(p.Z / CellSize));
    private static int StableSeed(string body, Double3 p)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in body) hash = (hash ^ c) * 16777619;
            foreach (double v in new[] { p.X, p.Y, p.Z })
            { long bits = BitConverter.DoubleToInt64Bits(Math.Round(v, 1)); hash = (hash ^ (uint)bits) * 16777619; hash = (hash ^ (uint)(bits >> 32)) * 16777619; }
            return (int)(hash & 0x7fffffff);
        }
    }
}
