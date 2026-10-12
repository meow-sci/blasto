using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Blasto.Core;
using Blasto.Effects;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace Blasto.Simulation;

/// <summary>Workers copy geometry and actual burn intervals. Terrain, sites and particles run only after apply.</summary>
public sealed class BlastSimulation : IDisposable
{
    private readonly record struct NozzleSample(int Generation, string BodyId, string VehicleId, int Nozzle,
        Double3 Origin, Double3 Exhaust, double Thrust, double BurnSeconds, double Epoch);
    private sealed record CachedHit(Double3 Origin, Double3 Direction, double Thrust, double Epoch, SurfaceHit? Hit, string Result);
    private static BlastSimulation? _active;
    private readonly ConcurrentQueue<NozzleSample> _samples = new();
    private readonly Dictionary<(string, string, int), CachedHit> _probeCache = new();
    private readonly BlastSiteStore _store;
    private readonly SurfaceProbe _probe = new();
    private readonly LandingDust _dust;
    private readonly Action<string> _log;
    private int _queued, _generation;
    private volatile bool _enabled = true;
    private int _debugPending;
    private long _lastCapacityRejected;
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public bool DustEnabled { get => _dust.Enabled; set => _dust.Enabled = value; }
    public double Intensity { get; set; } = 1;
    public double MaxRange { get => _probe.MaxRange; set => _probe.MaxRange = Math.Clamp(value, 20, 600); }
    public long CapturedSamples { get; private set; }
    public long AppliedSamples { get; private set; }
    public long WarpSkipped { get; private set; }
    public long QueueRejected { get; private set; }
    public long CaptureErrors { get; private set; }
    public long SurfaceErrors { get; private set; }
    public long TickErrors { get; private set; }
    public long SolverApplyCalls { get; private set; }
    public long CaptureCalls => Interlocked.Read(ref _captureCalls);
    public long NozzlesExamined => Interlocked.Read(ref _nozzlesExamined);
    public int PendingSamples => Volatile.Read(ref _queued);
    public long SurfaceHits { get; private set; }
    public long SurfaceRejected { get; private set; }
    public long DepositRejected { get; private set; }
    public string LastSurfaceResult { get; private set; } = "No surface samples yet";
    public string DebugPlacementStatus { get; private set; } = "No test placement requested";
    private bool _faultLogged;
    public string Status { get; private set; } = "Waiting for engine exposure";
    public BlastSimulation(BlastSiteStore store, Action<string> log)
    { _store = store; _log = log; _dust = new LandingDust(log); }
    public void Install(Harmony harmony)
    {
        if (_active is not null && _active != this) throw new InvalidOperationException("Blast simulation already installed.");
        _active = this;
        harmony.Patch(AccessTools.Method(typeof(VehicleUpdateState), nameof(VehicleUpdateState.UpdateModules)),
            postfix: new HarmonyMethod(typeof(BlastSimulation), nameof(CapturePostfix)));
        harmony.Patch(AccessTools.Method(typeof(Universe), nameof(Universe.ApplyVehicleSolvers)),
            postfix: new HarmonyMethod(typeof(BlastSimulation), nameof(ApplyPostfix)));
    }
    private static void CapturePostfix(VehicleUpdateState __instance, SimStep step) => _active?.Capture(__instance, step);
    private static void ApplyPostfix()
    {
        var active = _active;
        if (active is null) return;
        try { active.Tick(); }
        catch (Exception ex)
        {
            active.TickErrors++;
            active.Status = "Effect update failed; existing sites retained";
            active.LogFault("Effect update", ex.Message);
        }
    }
    private void LogFault(string operation, string message)
    {
        if (_faultLogged) return;
        _faultLogged = true;
        _log($"[Blasto] {operation} failed: {message}. Further failures are counted in diagnostics.");
    }
    private void Capture(VehicleUpdateState worker, SimStep step)
    {
        Interlocked.Increment(ref _captureCalls);
        if (!_enabled || step.DeltaTime <= 0) return;
        if (step.DeltaTime > 1) { Interlocked.Increment(ref _warpSkipped); return; }
        try
        {
            var states = worker.GetReadOnlyStates();
            if (states.Origin.Parent is not Celestial body) return;
            states.GetStatesCcf(out var position, out _, out var body2Ccf);
            var centerOfMass = double3.Unpack(states.Props.CenterOfMassAsmb);
            var nozzles = worker.UpdateData.RocketNozzles.GetReadOnlyStates();
            for (int i = 0; i < nozzles.Length; i++)
            {
                ref readonly var nozzle = ref nozzles[i];
                Interlocked.Increment(ref _nozzlesExamined);
                double burn = Math.Clamp(nozzle.ThrustTime, 0, step.DeltaTime);
                double thrust = nozzle.Performance.TotalThrust;
                if (burn <= 0 || thrust <= 1 || !double.IsFinite(thrust)) continue;
                var origin = position + (double3.Unpack(nozzle.ThrustLocationVehicleAsmb) - centerOfMass).Transform(body2Ccf);
                var exhaust = (-double3.Unpack(nozzle.ThrustDirectionVehicleAsmb)).Transform(body2Ccf);
                if (double3.Dot(origin.NormalizeOrZero(), exhaust.NormalizeOrZero()) > .12) continue;
                if (Interlocked.Increment(ref _queued) > 8192)
                { Interlocked.Decrement(ref _queued); Interlocked.Increment(ref _queueRejected); continue; }
                _samples.Enqueue(new NozzleSample(Volatile.Read(ref _generation), body.Id, worker.ReadOnlyVehicle.Id, i,
                    SurfaceProbe.FromNative(origin), SurfaceProbe.FromNative(exhaust).Normalized(), thrust, burn, step.NextTime.Seconds()));
                Interlocked.Increment(ref _captured);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _lastCaptureError, $"{ex.GetType().Name}: {ex.Message}");
            Interlocked.Increment(ref _captureErrors);
        }
    }
    private long _warpSkipped, _queueRejected, _captured, _captureErrors, _captureCalls, _nozzlesExamined;
    private string _lastCaptureError = "";
    public void Tick()
    {
        SolverApplyCalls++;
        CapturedSamples = Interlocked.Read(ref _captured);
        WarpSkipped = Interlocked.Read(ref _warpSkipped);
        QueueRejected = Interlocked.Read(ref _queueRejected);
        long errors = Interlocked.Read(ref _captureErrors);
        if (errors > CaptureErrors) LogFault("Worker nozzle capture", Volatile.Read(ref _lastCaptureError));
        CaptureErrors = errors;
        double appliedEpoch = Universe.GetLastSimStep().NextTime.Seconds();
        _dust.Tick(appliedEpoch);
        // This hook runs after the worker join/apply, before next dispatch. No viewport can add exposure.
        while (_samples.TryPeek(out var peek) && peek.Epoch <= appliedEpoch + 1e-6)
        {
            if (!_samples.TryDequeue(out var sample)) break;
            Interlocked.Decrement(ref _queued);
            if (!_enabled || sample.Generation != Volatile.Read(ref _generation)) continue;
            if (Universe.CurrentSystem?.All.TryGet(sample.BodyId, out Astronomical? resolved) != true || resolved is not Celestial body) continue;
            try
            {
                var key = (sample.BodyId, sample.VehicleId, sample.Nozzle);
                SurfaceHit? hit;
                if (_probeCache.TryGetValue(key, out var cached) && sample.Epoch - cached.Epoch < .08 &&
                    (sample.Origin - cached.Origin).LengthSquared < .16 && Math.Abs(sample.Thrust / cached.Thrust - 1) < .15 && Double3.Dot(sample.Exhaust, cached.Direction) > .9998)
                { hit = cached.Hit; LastSurfaceResult = cached.Result; }
                else
                {
                    hit = _probe.TryProbe(body, sample.Origin, sample.Exhaust, sample.Thrust, out var found, out var result) ? found : null;
                    LastSurfaceResult = result;
                    _probeCache[key] = new CachedHit(sample.Origin, sample.Exhaust, sample.Thrust, sample.Epoch, hit, result);
                }
                if (!hit.HasValue) { SurfaceRejected++; Status = LastSurfaceResult; continue; }
                SurfaceHits++;
                double thrust = sample.Thrust * Math.Clamp(double.IsFinite(Intensity) ? Intensity : 1, 0, 10);
                var mark = _store.Deposit(hit.Value, thrust, sample.BurnSeconds, sample.Epoch);
                if (mark is null)
                {
                    DepositRejected++;
                    Status = _store.Count >= _store.Capacity ? "Site capacity reached" :
                        $"{body.Id}: terrain hit; exposure below erosion threshold (thrust {thrust:F0} N, radius {hit.Value.Radius:F2} m, {hit.Value.Profile})";
                    continue;
                }
                AppliedSamples++;
                _dust.Emit(body, hit.Value, thrust, sample.BurnSeconds, sample.Epoch);
                Status = $"Engine exposure: {body.Id}; {_store.Count} persistent sites";
            }
            catch (Exception ex) { SurfaceErrors++; Status = "Surface sampling failed; affected nozzle skipped"; LogFault("Surface sample", ex.Message); }
        }
        if (_probeCache.Count > 1024) _probeCache.Clear();
        if (_store.CapacityRejected > _lastCapacityRejected)
        {
            _lastCapacityRejected = _store.CapacityRejected;
            Status = $"Site capacity {_store.Capacity} reached; existing sites preserved";
            _log("[Blasto] Site capacity reached. New locations skipped; existing saved sites retained.");
        }
        if (Interlocked.Exchange(ref _debugPending, 0) != 0) PlaceDebugMark();
    }
    public bool TryPlaceDebugMark(out string result)
    {
        if (Program.ControlledVehicle?.Parent is not Celestial)
        { result = DebugPlacementStatus = "Test placement rejected: select a controlled vehicle orbiting a celestial body."; return false; }
        Interlocked.Exchange(ref _debugPending, 1);
        result = DebugPlacementStatus = "Ground mark queued for the next simulation apply."; return true;
    }
    private void PlaceDebugMark()
    {
        var vehicle = Program.ControlledVehicle;
        if (vehicle?.Parent is not Celestial body)
        { Status = DebugPlacementStatus = "Test placement rejected: controlled vehicle is no longer available."; return; }
        try
        {
            var position = SurfaceProbe.FromNative(vehicle.GetPositionCce().Transform(body.GetCce2Ccf()));
            if (!_probe.TryProbe(body, position, -position.Normalized(), 300000, out var hit, out var result))
            { Status = DebugPlacementStatus = $"Test placement rejected: {result}"; return; }
            var mark = _store.DepositDiagnostic(hit, Universe.GetElapsedSeconds());
            Status = DebugPlacementStatus = mark is null ? $"Test placement rejected on {body.Id}: site capacity {_store.Capacity} reached." :
                $"Test mark placed on {body.Id}: radius {mark.Radius:F1} m, exposure {mark.Exposure:F2}, strength {mark.Strength:P0}; ground {hit.Distance:F2} m below vehicle.";
        }
        catch (Exception ex)
        {
            SurfaceErrors++;
            Status = DebugPlacementStatus = $"Test placement failed on {body.Id}: {ex.GetType().Name}: {ex.Message}";
            LogFault("Test placement", ex.Message);
        }
    }
    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        while (_samples.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
        _probeCache.Clear(); _dust.Clear(); _debugPending = 0; _faultLogged = false; _lastCapacityRejected = 0;
        DebugPlacementStatus = "No test placement requested";
    }
    public void Dispose() { _enabled = false; if (_active == this) _active = null; Reset(); }
}
