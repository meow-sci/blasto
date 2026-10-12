---
type: How-To
title: Lazy install and removal of one patch while it is in use
description: "Patch only while there is something to affect: gatOS creates a Harmony instance when the first vessel is marked, unpatches when the set empties, and does both on the main thread in the frame drain so no render code is mid-call"
category: patching
group: safety
topic: remove-your-patches-safely-at-runtime-and-at-exit
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/scale-and-force-render-cheats]
techniques: [dynamic-harmony-install, harmony-unpatch-by-id, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.GetWorldMatrix, harmony-prefix KSA.Vehicle.UpdateRenderData]
lifecycle: [frame:render, frame:after-on-frame]
game_types: [KSA.Vehicle]
tags: [harmony, dynamic-install, unpatch, lazy, zero-cost-default]
---

A patch on a hot method costs something every frame, even when it does nothing. gatOS's force-render cheat therefore installs its
two prefixes only when the first vessel is marked, and removes them when the last mark is dropped, so the default state costs
nothing. Both the install and the removal happen on the main thread inside the frame command drain, so no render code is mid-call
while Harmony swaps the method bodies. The prefixes read a volatile immutable set, so they are thread-agnostic.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by gatOS in [Per-vessel scale and force-render cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md).
The prefixes themselves are the subject of [Reproduce the original body for marked objects and return false](../../harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-1.md).

## Background: when the patched methods run

`Vehicle.GetWorldMatrix` and `Vehicle.UpdateRenderData` run on the main thread inside `Program.Render` while it fills this frame's
resources: `UpdateRenderingResources` for non-EVA vehicles, `RenderViewport` and `RenderGame` for kittens
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4337-L4358)[^ksa-program], [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4483-L4495), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4690-L4702)). A patch installed or removed outside that
window cannot be observed half-applied by those calls on the same thread.

## Lifecycle and timing

- Commands run in the frame-phase drain on the main thread, which is also where patching and unpatching happen, so no render code
  is mid-call when the method body changes.
- The prefixes run in `frame:render`; while installed they cost a hash lookup per call.
- Despawn pruning rides the sampler's vehicle enumeration (`VesselForceRender.Prune`), and `Teardown` handles unload. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the registry on the game thread and publish an immutable snapshot.** `Marked` is mutated only on the game thread;
   `_published` is the `volatile` immutable copy the prefixes read:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L35-L47
private static readonly HashSet<string> Marked = new(StringComparer.Ordinal);

/// <summary>The immutable snapshot the prefixes read; null while nothing is marked.</summary>
private static volatile HashSet<string>? _published;

private static Harmony? _harmony;
private static bool _prefixFaultLogged;

/// <summary>True when no vessel is marked — the sampler prune's cheap early-out. Game thread.</summary>
internal static bool IsEmpty => Marked.Count == 0;

/// <summary>Whether <paramref name="vesselId"/> is force-rendered (the <c>always_render</c> read-back).</summary>
internal static bool IsMarked(string vesselId) => _published?.Contains(vesselId) == true;
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L35-L47)[^gatos-vesselforcerender]

2. **Install on the first mark, remove on the last.** `Set` installs before adding the first id and publishes after; on removal it
   publishes first, then removes the patches when the set is empty:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L55-L75
internal static CommandResult Set(Vehicle vehicle, bool on)
{
    if (on)
    {
        if (!Marked.Contains(vehicle.Id))
        {
            if (Marked.Count == 0)
                InstallPatches();
            Marked.Add(vehicle.Id);
            Publish();
        }
    }
    else if (Marked.Remove(vehicle.Id))
    {
        Publish();
        if (Marked.Count == 0)
            RemovePatches();
    }

    return CommandResult.Ok;
}
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L55-L75)

3. **Create the Harmony instance when needed and roll back a half-install.** A missing method throws before anything is patched; a
   failure between the two `Patch` calls unpatches the pair by id so no half-installed state leaks:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L128-L159
private static void InstallPatches()
{
    if (_harmony is not null)
        return;

    var getWorldMatrix = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.GetWorldMatrix),
        [typeof(KsaCamera)]);
    var updateRenderData = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.UpdateRenderData),
        [typeof(IViewport), typeof(int)]);
    if (getWorldMatrix is null || updateRenderData is null)
        throw new MissingMethodException(
            "Vehicle.GetWorldMatrix/UpdateRenderData not found in this build");

    var harmony = new Harmony("gatos.always_render");
    try
    {
        harmony.Patch(getWorldMatrix, prefix: new HarmonyMethod(
            typeof(VesselForceRender).GetMethod(nameof(GetWorldMatrixPrefix),
                BindingFlags.NonPublic | BindingFlags.Static)!));
        harmony.Patch(updateRenderData, prefix: new HarmonyMethod(
            typeof(VesselForceRender).GetMethod(nameof(UpdateRenderDataPrefix),
                BindingFlags.NonPublic | BindingFlags.Static)!));
    }
    catch
    {
        harmony.UnpatchAll("gatos.always_render"); // don't leak a half-installed pair
        throw;
    }

    _harmony = harmony;
    ModLog.Log.Info("gatOS vessel force-render patches installed.");
}
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L128-L159)

4. **Remove by id and clear the field.** Removal logs but does not throw, and nulls `_harmony` so the next mark installs afresh:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L161-L176
private static void RemovePatches()
{
    if (_harmony is null)
        return;
    try
    {
        _harmony.UnpatchAll("gatos.always_render");
    }
    catch (Exception ex)
    {
        ModLog.Log.Debug($"gatOS force-render unpatch error: {ex.Message}");
    }

    _harmony = null;
    ModLog.Log.Info("gatOS vessel force-render patches removed.");
}
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L161-L176)

5. **Prune and tear down.** Marks for despawned vessels are dropped as part of the sampler's enumeration, tearing the patches down
   when the last one goes; `Teardown` clears every mark and removes the patches on unload:

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L83-L110
internal static void Prune(IReadOnlyList<VesselSnapshot> live)
{
    if (Marked.Count == 0)
        return;

    var removed = Marked.RemoveWhere(id =>
    {
        foreach (var vessel in live)
            if (vessel.Id == id)
                return false;
        return true;
    });
    if (removed == 0)
        return;

    Publish();
    ModLog.Log.Info($"gatOS force-render: {removed} despawned vessel mark(s) pruned.");
    if (Marked.Count == 0)
        RemovePatches();
}

/// <summary>Unload teardown: drops every mark and removes the patches. Game thread only.</summary>
internal static void Teardown()
{
    Marked.Clear();
    Publish();
    RemovePatches();
}
```

Source: [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L83-L110)

6. **Same shape for the IVA cheat.** Each cheat creates its own Harmony instance with its own id (`gatos.always_render`,
   `gatos.iva`) when it is first used, so removing one never touches the other.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs) | Registry, lazy install, removal, prefixes |
| [gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs#L56-L140)[^gatos-ivaforcerender] | The same install and restore shape for the interior-visibility cheat |

## Variations and gotchas

- **Install and remove only on the game thread.** gatOS drains queued control commands on the game thread
  ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L255-L275)[^gatos-mod-game]); install and removal happen inside that drain, never from a worker
  thread.
- **`Set` is idempotent.** Marking an already marked vessel does nothing, and an install failure propagates so the caller can
  latch the actuator as degraded with the registry unchanged.
- **Unload.** `UnpatchAll("gatos.always_render")` at `[StarMapUnload]` finds nothing under StarMap; the removal that matters is the
  one during play ([UnpatchAll(ownId) from a main-thread hook after nulling delegates](remove-your-patches-safely-at-runtime-and-at-exit-1.md)).
- **The patches are dynamic, so anchors age differently.** Re-verify the targets on every game update; a method that has been
  patched only in a session where a vessel was marked can drift unnoticed.

## See also

- [UnpatchAll(ownId) from a main-thread hook after nulling delegates](remove-your-patches-safely-at-runtime-and-at-exit-1.md), [Unpatch by patch method on a shared Harmony instance](remove-your-patches-safely-at-runtime-and-at-exit-3.md)
- [Removing Harmony patches safely at runtime and at exit](overview.md)
- [Per-vessel scale and force-render cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md)
- [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md) for the install half in the internals group

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^gatos-vesselforcerender]: gatOS/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs
[^gatos-ivaforcerender]: gatOS/gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs
[^gatos-mod-game]: gatOS/gatOS.GameMod/Game/Mod.Game.cs
