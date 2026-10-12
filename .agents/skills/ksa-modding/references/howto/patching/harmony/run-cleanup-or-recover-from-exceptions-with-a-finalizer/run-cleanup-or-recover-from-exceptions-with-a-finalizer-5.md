---
type: How-To
title: ThreadStatic override set in try/finally around the mod's own submission
description: Make a prefix on the shared VolumetricExhaustRenderer.AddInstance edit a plume only while the mod itself is submitting it, using a ThreadStatic scope that the mod's call site opens and closes in try/finally
category: patching
group: harmony
topic: run-cleanup-or-recover-from-exceptions-with-a-finalizer
approach: 5
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/pyro-scoped-exhaust-look-override]
techniques: [harmony-prefix, thread-static-scope]
hooks:
  - harmony-prefix KSA.VolumetricExhaustRenderer.AddInstance
  - harmony-postfix KSA.Vehicle.AddVolumetricExhaustInstances
lifecycle: [frame:pre-render, render:exhaust]
game_types: [KSA.VolumetricExhaustRenderer, KSA.Vehicle]
tags: [harmony, prefix, thread-static, scoped-override, exhaust, try-finally]
---

Sometimes the method you need to patch is shared by code you must not affect. The unscience Pyro mod wants
two per-plume "look" values (absorption density and refraction) on its own exhaust plumes, but the game rebuilds
those values from a shared template on every submission, and the final enqueue method is also used by stock
engines. Pyro's answer: a prefix that does nothing unless a `[ThreadStatic]` override is set, and an override
that is set only inside Pyro's own submission call and cleared in a `finally` at the call site. This is the
"scoped override" pattern without a Harmony finalizer; the scope's lifetime is the mod's own `try`/`finally`.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by Pyro in
[Override one caller's data at a shared game method with a scoped prefix](../../../../mods/unscience/features/pyro-scoped-exhaust-look-override.md).

## Background: how the game does it

Every plume submission ends in `AddInstanceCore`, which fills the shader struct from
`Template.BuildBlendedValues()` (overwriting `refractionIntensity` and `absorptionDensity`), multiplies the
refraction by pressure and temperature ramps, and finally calls the small public
`AddInstance(ExhaustInstance, int)` that stores the struct:

```csharp
// current/decomp/KSA/VolumetricExhaustRenderer.cs L1046-L1047, L1113-L1115, L1136
shaderData.refractionIntensity = templateValues.RefractionIntensity;
shaderData.absorptionDensity = templateValues.AbsorptionDensity;
// ...
float num7 = float.Clamp((float)_currentAtmosphericPressure / 0.01f, 0f, 1f);
float num8 = float.Clamp(plumeData.ExhaustTemperature / 2000f, 0f, 1f);
shaderData.refractionIntensity *= num7 * num8;
// ...
int instanceIndex = AddInstance(shaderData, GetFeaturePermutation(volumetricExhaustInstance.Template));
```

Source: [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L1046-L1136)[^ksa-volumetricexhaustrenderer]

```csharp
// current/decomp/KSA/VolumetricExhaustRenderer.cs L787-L792
public int AddInstance(ExhaustInstance inInstance, int inFeaturePermutation)
{
	if (Disabled || _instanceCount >= _maxInstanceCount)
	{
		return -1;
	}
```

Source: [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L787-L792)

Setting values on the instance beforehand does nothing (they are overwritten), and editing the template would
change every engine. The last call is the only place where finished values can be changed for one plume.

## Lifecycle and timing

Pyro submits its plumes from a postfix on `Vehicle.AddVolumetricExhaustInstances`, on the main thread during
`Program.OnPreRender` (`frame:pre-render`). The scope opens and closes around one synchronous call, so no
other code observes it; `[ThreadStatic]` also keeps it invisible to the render worker threads that the stock
plume update uses. The values land in `_instances` and are uploaded when `VolumetricExhaustRenderer.Render` runs
in the exhaust pass (`render:exhaust`). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the override per thread.** `_activeLookOverride` is `[ThreadStatic]`, so only the thread that opened
   the scope can see it. A `LookOverrideScope` restores the previous value on `Dispose`, which makes nested scopes
   safe:

```csharp
// pyro.lib/PyroPatches.cs L12-L17, L68-L75, L100-L127
public static class PyroPatches
{
    [ThreadStatic]
    private static LookOverride? _activeLookOverride;

    public static bool LookOverrideAvailable { get; private set; }
// ...
    /// <summary>Begins the narrowly scoped final-instance look override used by one Pyro submission.</summary>
    internal static IDisposable BeginLookOverride(float absorptionDensity, float refractionScale,
        float refractionFallback)
    {
        var previous = _activeLookOverride;
        _activeLookOverride = new LookOverride(absorptionDensity, refractionScale, refractionFallback);
        return new LookOverrideScope(previous);
    }
// ...
    private sealed class LookOverride
    {
        public readonly float AbsorptionDensity;
        public readonly float RefractionScale;
        public readonly float RefractionFallback;

        public LookOverride(float absorptionDensity, float refractionScale, float refractionFallback)
        {
            AbsorptionDensity = absorptionDensity;
            RefractionScale = refractionScale;
            RefractionFallback = refractionFallback;
        }
    }

    private sealed class LookOverrideScope : IDisposable
    {
        private readonly LookOverride? _previous;
        private bool _disposed;

        public LookOverrideScope(LookOverride? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _activeLookOverride = _previous;
        }
    }
```

Source: [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs#L12-L127)[^unscience-pyropatches]

2. **Write a prefix that is a no-op outside the scope.** The parameter is named `inInstance` like the original's
   and taken by `ref`. With no override it returns immediately; with one it edits only the two fields:

```csharp
// pyro.lib/PyroPatches.cs L77-L85
private static void BeforeFinalInstanceSubmission(ref ExhaustInstance inInstance)
{
    var look = _activeLookOverride;
    if (look == null) return;
    inInstance.absorptionDensity = look.AbsorptionDensity;
    inInstance.refractionIntensity = float.IsFinite(look.RefractionScale)
        ? inInstance.refractionIntensity * look.RefractionScale
        : look.RefractionFallback;
}
```

Source: [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs#L77-L85)

3. **Install both patches or neither.** The final-instance prefix and the vehicle postfix are installed together;
   if either fails, the other is removed, `LookOverrideAvailable` stays `false` and the exception propagates, so
   the UI can say "unavailable on this game build":

```csharp
// pyro.lib/PyroPatches.cs L32-L56
public static void Apply(Harmony harmony)
{
    var vehicleTarget = VehicleTarget();
    var finalTarget = FinalInstanceTarget();
    if (vehicleTarget == null)
        throw new MissingMethodException(typeof(Vehicle).FullName, nameof(Vehicle.AddVolumetricExhaustInstances));
    if (finalTarget == null)
        throw new MissingMethodException(typeof(VolumetricExhaustRenderer).FullName, "AddInstance(ExhaustInstance,int)");

    bool vehiclePatched = false;
    try
    {
        harmony.Patch(vehicleTarget, postfix: new HarmonyMethod(VehiclePostfix()));
        vehiclePatched = true;
        harmony.Patch(finalTarget, prefix: new HarmonyMethod(FinalInstancePrefix()));
        LookOverrideAvailable = true;
    }
    catch
    {
        if (vehiclePatched) harmony.Unpatch(vehicleTarget, VehiclePostfix());
        harmony.Unpatch(finalTarget, FinalInstancePrefix());
        LookOverrideAvailable = false;
        throw;
    }
}
```

Source: [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs#L32-L56)

4. **Open the scope at the call site, close it in `finally`.** The mod computes the values, opens the scope, makes
   the one submission, and always disposes it, even if `AddInstance` throws:

```csharp
// pyro.lib/PlumeEmitter.cs L80-L95
// AddInstanceCore reconstructs absorption/refraction from the shared template. Scope the final
// ExhaustInstance seam to this one direct submission so stock engines and shared template state stay
// untouched, even if AddInstance throws.
ExhaustBendTarget bendTarget = default;
ExhaustAxialFade fade = ExhaustAxialFade.NoFadeOut;
ExhaustDiamondFade diamondFade = ExhaustDiamondFade.None;
IDisposable lookScope = PyroPatches.BeginLookOverride(absorption, refractionScale, refractionFallback);
try
{
    renderer.AddInstance(instance, in bendTarget, in fade, in diamondFade);
}
finally
{
    lookScope.Dispose();
}
return true;
```

Source: [pyro.lib/PlumeEmitter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeEmitter.cs#L80-L95)[^unscience-plumeemitter]

5. **Verify.** A Pyro plume shows its own absorption; stock engines submitted later in the frame (through the same
   method) are untouched because no override is set when the prefix sees them.

## Complete example

| File | Role |
|---|---|
| [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs) | Patches, `[ThreadStatic]` scope, prefix |
| [pyro.lib/PlumeEmitter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeEmitter.cs#L55-L96) | Computes values, opens and closes the scope around the submission |

## Variations and gotchas

- **Why not a finalizer.** The scope is bounded by code the mod owns, so a plain `try`/`finally` in the caller
  suffices. A finalizer is for when the bracket is the *game's* method (approach 1 of this topic).
- **The pattern needs a synchronous call on one thread.** A target that hands work to a job needs a different
  carrier than `[ThreadStatic]`.
- **The parameter name must match.** `inInstance` and `ref` are required; a rename of the original parameter
  makes Harmony throw at install, which the all-or-nothing `Apply` turns into "unavailable".
- **Nothing shared is written.** The template and the stock path are never touched, so there is nothing to restore.
- **Refraction currently has no visible effect.** In build 5554 `_hasRefractionInstances` is reset and read by
  the render passes but never set to true anywhere in the decompile, so the refraction passes do not run
  (see the feature page).
- **History.** The feature page records that before build 5438 Pyro mutated a private `_shaderData` field by reflection; once
  `AddInstanceCore` rebuilt the values from the template that stopped working and was retired for this seam.

## See also

- [Restore what a prefix changed around one game call](run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md), [Swallow an exception and finish the method's remaining work](run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md), [Prefix with __state and a finalizer that restores an edited input](run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md), [Prefix, postfix and finalizer as a transaction around loads](run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md), [Thread-static construction context scoping a transpiler and a prefix](run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md), [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md)
- [Harmony finalizers, cleanup and scoped overrides](overview.md)
- [Override one caller's data at a shared game method with a scoped prefix](../../../../mods/unscience/features/pyro-scoped-exhaust-look-override.md)

[^ksa-volumetricexhaustrenderer]: KSA.VolumetricExhaustRenderer (decompiled, build 2026.10.10.5554)
[^unscience-pyropatches]: unscience/pyro.lib/PyroPatches.cs
[^unscience-plumeemitter]: unscience/pyro.lib/PlumeEmitter.cs
