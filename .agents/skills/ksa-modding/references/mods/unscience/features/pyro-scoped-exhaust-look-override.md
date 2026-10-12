---
type: Mod Feature
title: Override one caller's data at a shared game method with a scoped prefix
description: Pyro gives each plume its own absorption density and refraction by prefixing VolumetricExhaustRenderer.AddInstance(ExhaustInstance, int) with a ThreadStatic override that is only set inside a try/finally around Pyro's own submission, so stock engines and shared templates are never touched
mod: unscience
submod: pyro
category: patching
techniques: [harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.VolumetricExhaustRenderer.AddInstance(ExhaustInstance, int)
  - harmony-postfix KSA.Vehicle.AddVolumetricExhaustInstances (the submission the override is scoped to)
lifecycle: [frame:pre-render]
game_types:
  - KSA.VolumetricExhaustRenderer
  - KSA.ExhaustInstance
  - KSA.VolumetricExhaustInstance
  - KSA.VolumetricExhaustTemplate
tags: [harmony, prefix, threadstatic, exhaust, scoping, rendering]
---

Each Pyro plume has two "look" sliders, absorption density (a multiplier on the template value) and
refraction (heat haze), that should affect only that plume. The values the shader reads are
written by the renderer itself from the shared template on every submission, so setting them on the
instance beforehand does nothing, and changing the template would change every engine. Pyro
intercepts the one place where the final per-instance struct is copied into the renderer's array,
and only while Pyro itself is submitting.[^pyro-lib-readme]

Mechanism in one line: a Harmony prefix on `VolumetricExhaustRenderer.AddInstance(ExhaustInstance,
int)` takes `ref ExhaustInstance` and edits `absorptionDensity` / `refractionIntensity` only when a
`[ThreadStatic]` override is set, and Pyro sets that override in a disposable scope around its own
`renderer.AddInstance(VolumetricExhaustInstance, ...)` call.

## How it works

**The seam.** In 5554 every submission ends in `AddInstanceCore`, which copies the instance's shader
data, overwrites `refractionIntensity` and `absorptionDensity` from
`Template.BuildBlendedValues()`, later multiplies refraction by an ambient-pressure ramp
(`clamp(p_atm / 0.01, 0, 1)`) and an exhaust-temperature ramp (`clamp(T / 2000, 0, 1)`), and finally
calls the small public `AddInstance(ExhaustInstance inInstance, int inFeaturePermutation)`, which
stores the struct in `_instances`.[^ksa-exhaust-renderer][^ksa-exhaust-template] That last call is the
only point where the finished values can be changed for one plume.

**The override.**[^pyro-patches]

1. `PyroPatches.Apply` installs two patches on one Harmony instance: the vessel postfix (see
   [volumetric exhaust plumes](./pyro-volumetric-exhaust-plumes.md)) and the prefix on the exactly
   bound `AddInstance(ExhaustInstance, int)` overload. If either patch fails, the vessel postfix is
   removed again, `LookOverrideAvailable` stays false and the exception propagates, so Pyro is
   installed completely or not at all; the look sliders show "unavailable on this game build"
   whenever the flag is false.
2. `BeginLookOverride(absorption, refractionScale, fallback)` saves the current `[ThreadStatic]`
   value, installs a new `LookOverride` and returns an `IDisposable` that restores the previous value.
   Saving and restoring (rather than clearing) makes nested scopes safe.
3. `PlumeEmitter.Submit` computes the absorption as template density times the plume's multiplier,
   and the refraction as a scale relative to the template value, then wraps
   `renderer.AddInstance(instance, ...)` in `try { } finally { scope.Dispose(); }`.[^plume-emitter]
4. The prefix (`ref ExhaustInstance inInstance`, named like the original parameter) writes
   `absorptionDensity`, and multiplies `refractionIntensity` by the scale, which keeps the renderer's
   ambient and temperature ramps. If the template's refraction is zero the scale is `NaN`, and the
   prefix uses a fallback that recomputes the same ramps (`PlumeMigrationMath.RefractionFallback`).[^plume-migration-math]

Stock engines submit through the same method later in the frame (`EndPlumeGrouping`), but no
override is set then, so the prefix returns immediately. Nothing in the shared template is written.
`pyro.tests` checks the scale and the fallback ramp against the renderer's constants.[^pyro-tests]

## Lifecycle and timing

Everything happens on the main thread in `Program.OnPreRender`, inside the
`Vehicle.AddVolumetricExhaustInstances` postfix (`frame:pre-render`, see
[plumes](./pyro-volumetric-exhaust-plumes.md#lifecycle-and-timing)). The scope opens and closes
around a single synchronous call, so no other code can observe it; `[ThreadStatic]` additionally
keeps it invisible to the render worker threads that the stock plume update uses in
`EndPlumeGrouping`. The values land in `_instances` and are uploaded when
`VolumetricExhaustRenderer.Render` runs in the exhaust pass (`render:exhaust`).

## Key code

| Path | Role |
|---|---|
| [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs#L14-L85) | Patch targets, all-or-nothing install, scope factory, prefix |
| [pyro.lib/PyroPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroPatches.cs#L100-L127) | Override record and restoring scope |
| [pyro.lib/PlumeEmitter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeEmitter.cs#L70-L95) | Look values and the try/finally submission |
| [pyro.lib/PlumeMigrationMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeMigrationMath.cs#L22-L39) | Relative refraction scale and fallback ramp |
| [pyro.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.tests/Program.cs#L41-L44) | Ramp and scale checks |

## Snippets

Scope factory and prefix:

```csharp
// pyro.lib/PyroPatches.cs L14-L85
    [ThreadStatic]
    private static LookOverride? _activeLookOverride;
// ...
    internal static IDisposable BeginLookOverride(float absorptionDensity, float refractionScale,
        float refractionFallback)
    {
        var previous = _activeLookOverride;
        _activeLookOverride = new LookOverride(absorptionDensity, refractionScale, refractionFallback);
        return new LookOverrideScope(previous);
    }

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

The scope restores the previous override:

```csharp
// pyro.lib/PyroPatches.cs L114-L127
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

The only caller, Pyro's own submission:

```csharp
// pyro.lib/PlumeEmitter.cs L76-L95
        float absorption = (float)template.Absorption.Density.Value;
        absorption = Math.Max(float.IsFinite(absorption) ? absorption : 0f, 0f)
                     * (float.IsFinite(plume.AbsorptionDensityScale) ? Math.Max(plume.AbsorptionDensityScale, 0f) : 1f);

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

Why the instance values cannot simply be set beforehand:

```csharp
// current/decomp/KSA/VolumetricExhaustRenderer.cs L1035-L1136
		ExhaustInstance shaderData = volumetricExhaustInstance.ShaderData;
		PlumeData plumeData = (isMerged ? merged.Plume : volumetricExhaustInstance.LastPlumeData);
		float density = plumeData.Density;
		ExhaustBlendedTemplateValues templateValues = ((isMerged && templateSlot != volumetricExhaustInstance.Template.AnimationLutId) ? merged.BlendedValues : volumetricExhaustInstance.Template.BuildBlendedValues());
// ...
		shaderData.refractionIntensity = templateValues.RefractionIntensity;
		shaderData.absorptionDensity = templateValues.AbsorptionDensity;
// ...
		float num7 = float.Clamp((float)_currentAtmosphericPressure / 0.01f, 0f, 1f);
		float num8 = float.Clamp(plumeData.ExhaustTemperature / 2000f, 0f, 1f);
		shaderData.refractionIntensity *= num7 * num8;
// ...
		int instanceIndex = AddInstance(shaderData, GetFeaturePermutation(volumetricExhaustInstance.Template));
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `VolumetricExhaustRenderer.AddInstance(ExhaustInstance, int)` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L787-L808) | Prefix target; final enqueue into `_instances` |
| `VolumetricExhaustRenderer.AddInstanceCore` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L1029-L1141) | Rebuilds the look values from the template, then calls the prefix target |
| `VolumetricExhaustRenderer.AddInstance(VolumetricExhaustInstance, in ...)` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L842-L845) | The public call Pyro scopes |
| `VolumetricExhaustRenderer._hasRefractionInstances` (private) | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L164) | Gates the refraction passes; reset at L770, read at L1249/L1458/L1500, never set |
| `ExhaustInstance.absorptionDensity` / `refractionIntensity` | [current/decomp/KSA/ExhaustInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExhaustInstance.cs#L25-L69) | The two fields written (sequential, `Pack = 1` struct) |
| `VolumetricExhaustTemplate.BuildBlendedValues()` | [current/decomp/KSA/VolumetricExhaustTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustTemplate.cs#L188-L202) | Template source of the overwritten values |

## Pitfalls and version notes

- The prefix parameter must be named `inInstance` and taken by `ref`; a rename of the original
  parameter makes Harmony throw at `Apply`. The authors list this seam and the possibility of
  `AddInstanceCore` changing as the main silent-drift risk.[^scope-exhaust]
- Refraction has no visible effect in current builds: `_hasRefractionInstances` is reset in
  `UpdateFrameData` and read by the render passes but never set to true anywhere in the 5554 decomp,
  so the refraction passes never run for stock or Pyro plumes.[^ksa-exhaust-renderer]
- Before 5438 Pyro mutated the instance's private `_shaderData` by reflection; that stopped working
  once `AddInstanceCore` rebuilt the values from the template, and the reflection was retired in favour
  of this seam.[^scope-exhaust]
- Pyro never writes the struct stride or copies the struct; it only edits two named fields in place,
  so struct growth (272 to 320 bytes in 5438) does not affect it.[^ksa-exhaust-instance-struct]
- The pattern relies on the scoped call being synchronous and on the target being reached from it on
  the same thread. A target that defers work to a job would need a different carrier than
  `[ThreadStatic]`.

## See also

- [Volumetric exhaust plumes](./pyro-volumetric-exhaust-plumes.md)
- [Exhaust template editor](./pyro-exhaust-template-editor.md) (the global alternative)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Pyro sub-mod](../submods/pyro.md)
- [Mod overview](../overview.md)
- [ThreadStatic override set in try/finally around the mod's own submission](../../../howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md) - how-to article demonstrating this feature

[^pyro-patches]: pyro.lib/PyroPatches.cs
[^plume-emitter]: pyro.lib/PlumeEmitter.cs
[^plume-migration-math]: pyro.lib/PlumeMigrationMath.cs
[^pyro-tests]: pyro.tests/Program.cs
[^pyro-lib-readme]: pyro.lib/README.md
[^scope-exhaust]: scope/exhaust-plumes.md (authors' integration analysis)
[^ksa-exhaust-renderer]: KSA.VolumetricExhaustRenderer (decompiled, build 2026.10.10.5554)
[^ksa-exhaust-instance-struct]: KSA.ExhaustInstance (decompiled, build 2026.10.10.5554)
[^ksa-exhaust-template]: KSA.VolumetricExhaustTemplate (decompiled, build 2026.10.10.5554)
