using System;
using System.Collections.Generic;
using Blasto.Core;
using Blasto.Persistence;
using Blasto.Simulation;
using Brutal.Numerics;
using KSA;
using KSA.Rendering.Particles;

namespace Blasto.Effects;

/// <summary>Dedicated native volumetric bursts: at most twelve emitters and 384 live particles.</summary>
public sealed class LandingDust : IDisposable
{
    private sealed record Owned(Celestial Body, ParticleEmitter<ParticleUpdateData, ParticleRenderData>.Handle Handle, double Expires, float BaseOpacity);
    private readonly List<Owned> _owned = new();
    private readonly Dictionary<(string, long, long, long), double> _lastEmission = new();
    private readonly Action<string> _log;
    private ParticleEmitterReference? _reference;
    private bool _failed;
    public bool Enabled { get; set; } = true;
    public double ErosionThreshold { get; set; }
    public double OpacityMultiplier { get; set; } = 1;
    public double SizeMultiplier { get; set; } = 1;
    public double SpeedMultiplier { get; set; } = 1;
    public int ActiveEmitters => _owned.Count;
    public LandingDust(Action<string> log) => _log = log;
    public static void ValidateTuning(BlastSettings settings)
    {
        // These are float representability checks, not gameplay or mouse-drag limits.
        if (!float.IsFinite((float)(settings.DustSpeed * 33.2)))
            throw new ArgumentException("Dust speed produces particle velocities too large for the game renderer.");
        if ((float)(settings.DustSize * .2) <= 0)
            throw new ArgumentException("Dust size is too small for the game renderer.");
    }
    public void Emit(Celestial body, SurfaceHit hit, double thrust, double dt, double epoch)
    {
        if (!Enabled || _failed || !GameSettings.Current.Graphics.Particles || _owned.Count >= 12 || dt <= 0) return;
        var key = (body.Id, (long)Math.Floor(hit.CenterCcf.X / 8), (long)Math.Floor(hit.CenterCcf.Y / 8), (long)Math.Floor(hit.CenterCcf.Z / 8));
        if (_lastEmission.TryGetValue(key, out double last) && epoch - last < .12) return;
        double erosionRate = Math.Max(thrust * hit.Incidence / (Math.PI * hit.Radius * hit.Radius) - ErosionThreshold, 0);
        if (erosionRate < 5 || hit.Profile == SurfaceKind.Rock) return;
        ParticleEmitter<ParticleUpdateData, ParticleRenderData>? emitter = null;
        try
        {
            var system = Program.Instance?.ParticleSystem;
            if (system is null) return;
            _reference ??= CreateReference();
            if (!system.EmitterPool.Get(1, out var acquired)) return;
            emitter = acquired[0];
            system.InitializeEmitter(emitter, _reference);
            emitter.Context.Astronomical = body;
            emitter.Context.Vehicle = null;
            emitter.Context.Part = null;
            emitter.Origin = new BubbleOrigin
            {
                Parent = body, Time = Universe.GetElapsedTime(), BubFrame = BubbleFrame.Ccf,
                PositionBub = SurfaceProbe.ToNative(hit.CenterCcf), VelocityBub = double3.Zero
            };
            emitter.LocalOffset = float4x4.Identity;
            emitter.SpawnRate = (float)Math.Clamp(4 + erosionRate * .006, 4, 32);
            emitter.Drag = hit.Profile == SurfaceKind.Martian ? 1.6f : .3f;
            // Native screenspace volumetrics read GpuEmitterParams.Opacity, multiplied by particle
            // color alpha and a lifetime fade. Extra.z is not an opacity channel in this build.
            float baseOpacity = hit.Profile == SurfaceKind.Martian ? .42f : .24f;
            emitter.Opacity = (float)(baseOpacity * OpacityMultiplier);
            var rng = new Random(Random.Shared.Next());
            // Snapshot values before the native particle worker invokes this spawn callback.
            double speedMultiplier = SpeedMultiplier, sizeMultiplier = SizeMultiplier;
            double speed = Math.Clamp(Math.Sqrt(erosionRate) * .35, 3, 24) * speedMultiplier;
            var normal = hit.NormalCcf; var u = hit.TangentU; var v = hit.TangentV;
            double spread = Math.Min(hit.Radius * .22, 5);
            bool mars = hit.Profile == SurfaceKind.Martian;
            emitter.ParticleInfo.ParticleSpawnLogic = _ =>
            {
                double angle = rng.NextDouble() * Math.PI * 2;
                var outward = u * Math.Cos(angle) + v * Math.Sin(angle);
                var pos = outward * (Math.Sqrt(rng.NextDouble()) * spread) + normal * .18;
                // Already in body-fixed local coordinates; identity LocalOffset avoids double rotation.
                var velocity = outward * (speed * (.6 + rng.NextDouble() * .7)) + normal * ((mars ? 2.0 : .7) * speedMultiplier);
                var p = ParticleUpdateData.Create(ToFloat(pos), (float)(1.6 + rng.NextDouble() * 1.5), (float)((.2 + rng.NextDouble() * .45) * sizeMultiplier));
                p.Velocity = ToFloat(velocity);
                p.Color = mars ? new float4(.65f, .38f, .24f, 1) : new float4(.52f, .51f, .49f, 1);
                p.Extra = new float4(.15f, 0, 0, 0);
                return p;
            };
            var handle = emitter.CreateHandle();
            body.AddEmitter(handle);
            _owned.Add(new Owned(body, handle, epoch + 3.5, baseOpacity));
            _lastEmission[key] = epoch;
        }
        catch (Exception ex)
        {
            emitter?.Kill();
            _failed = true;
            _log($"[Blasto] Landing dust disabled after native emitter initialization failed: {ex.Message}");
        }
    }
    private static ParticleEmitterReference CreateReference()
    {
        // Reuse only the native sphere mesh; do not fire or mutate the impact/debris effect.
        var native = ModLibrary.Get<ParticleEmitterReference>("PlanetImpact").Get();
        var mesh = native.ParticleEmitters.Count > 0 ? native.ParticleEmitters[0]?.Get().Mesh : null;
        if (mesh is null) throw new InvalidOperationException("Native dust sphere mesh missing.");
        return new ParticleEmitterReference
        {
            Mesh = mesh, MaxParticles = new IntegerReference(32), SpawnMode = EmitterSpawnMode.Burst,
            Renderer = ParticleEmitterReference.ParticleRenderer.Volumetric,
            Updaters = new List<ParticleEmitterReference.ParticleUpdater?>
            {
                ParticleEmitterReference.ParticleUpdater.SimpleMovement,
                ParticleEmitterReference.ParticleUpdater.GrowOverLifetime,
                ParticleEmitterReference.ParticleUpdater.ModelMatrix,
                ParticleEmitterReference.ParticleUpdater.Age
            },
            // Density drives native buoyancy, not optical thickness. Mineral grains should retain
            // nearly full gravity in an atmosphere rather than behave like low-density smoke.
            Density = 2700f, Drag = .3f, InheritVelocity = new BoolReference(false),
            VehicleIsParent = new BoolReference(false), EmitterRelative = new BoolReference(false),
            ParticleLifespan = new Vector2Reference(new float2(1.6f, 3.1f)),
            ParticleSize = new Vector2Reference(new float2(.2f, .65f)),
            EmitterExtra = new Vector4Reference(new float4(.1f, 0, 0, 4)),
            ParticleColor = new Vector4Reference(new float4(.52f, .51f, .49f, 1))
        };
    }
    public void Tick(double epoch)
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            var owned = _owned[i];
            var emitter = owned.Handle.TryGet();
            if (emitter is null)
            { owned.Body.RemoveEmitter(owned.Handle); _owned.RemoveAt(i); continue; }
            emitter.Opacity = (float)(owned.BaseOpacity * OpacityMultiplier);
            if (!Enabled || epoch > owned.Expires) emitter.Kill();
            // Keep ownership until native fenced cleanup invalidates the handle; do not reacquire early.
        }
        if (_lastEmission.Count > 256) _lastEmission.Clear();
    }
    public void Clear()
    {
        foreach (var owned in _owned)
        {
            owned.Handle.TryGet()?.Kill();
            owned.Body.RemoveEmitter(owned.Handle);
        }
        _owned.Clear(); _lastEmission.Clear(); _reference = null; _failed = false;
    }
    public void Dispose() => Clear();
    private static float3 ToFloat(Double3 p) => new((float)p.X, (float)p.Y, (float)p.Z);
}
