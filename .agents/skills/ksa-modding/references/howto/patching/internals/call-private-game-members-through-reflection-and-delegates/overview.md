---
type: Game Feature
title: Reading, writing and calling private game members
description: How KSA mods reach private and internal members that the game exposes no API for, with AccessTools field refs, plain reflection, method delegates and UnsafeAccessor, and how each form fails when a member moves
category: patching
group: internals
topic: call-private-game-members-through-reflection-and-delegates
demonstrated_by:
  - mods/ksa-deltavmap/features/stock-transfer-planner-bridge
  - mods/unscience/features/parts-now-reflection-self-test
  - mods/unscience/features/parts-now-runtime-unload-purge
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
  - mods/ksaero/features/stock-drag-delegate-fallback
  - mods/unscience/features/dont-stifle-me-editor-free-scaling
  - mods/kittenextensions/features/custom-xml-asset-types
  - mods/shaderextensions/features/xml-asset-types-and-element-injection
lifecycle:
  - startup:all-mods-loaded
  - startup:starmap-before-main
  - startup:immediate-load
  - frame:draw-ui
  - physics:vehicle-solver
  - editor:on-frame
game_types:
  - KSA.TransferPlanner
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.GameSettings
  - KSA.Vehicle
  - KSA.PhysicsStates
  - KSA.VehicleEditor
  - KSA.GameSaves
  - KSA.VehicleSaves
  - KSA.XmlHelper
tags: [reflection, accesstools, fieldref, delegate, unsafe-accessor, private-members, internals]
---

KSA exposes little API, and much of what a mod needs sits in `private` or `internal` members: the Transfer Planner's state, the `ModLibrary` registries, a private nested enum, the editor's snapping helpers, a get-only static that holds
the XML serializer. There are four ways to reach them without copying the game's code, and each fails differently when a member moves. This topic shows each one on a real mod, so you can pick by what you need: read
or write a field, call a method, or overwrite a field the runtime marks `initonly`.

## Choosing a form

| Need | Form | Cost | Used in |
|---|---|---|---|
| Read or write a private **static** field often | `AccessTools.StaticFieldRefAccess<T>(field)` -> `ref T` getter | none per call after bind | [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md), [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md) |
| Read or write a private **instance** field of a class often | `AccessTools.FieldRefAccess<T, F>(field)` (`T` a reference type) | none per call after bind | [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md) |
| Read an internal static field occasionally, or a field of a generic you probe once | `GetField(...).GetValue(...)` | reflection per call | [Plain GetField on internal registries and a generic holder for instance fields](call-private-game-members-through-reflection-and-delegates-2.md) |
| Call a private method often (workers included) | `AccessTools.MethodDelegate<TDelegate>(method)` with a delegate type that mirrors the modifiers | none per call after bind | [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md), [Delegates over private editor helpers reused in a replacement drag](call-private-game-members-through-reflection-and-delegates-5.md), [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md) |
| Overwrite a get-only static (`initonly` backing field) | `[UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<P>k__BackingField")]` returning `ref T` | none per call | [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md), [Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md) |

A test against .NET 10 confirms why the last row exists: `FieldInfo.SetValue` on a get-only static's backing field throws `FieldAccessException` once the type is initialised, while the `UnsafeAccessor` write
succeeds ([UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md)).

## Engine background

| Type | Private member | Why it matters |
|---|---|---|
| `TransferPlanner` | static `_sourceBody`, `_destinationBody`, `_transferType`, `cts`, `SetTransferInfo()` | Plan state with no API ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L63-L67)[^ksa-transferplanner], [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L338-L350)) |
| `ModLibrary`, `SerializedCollection<T>` | `internal static readonly` registries, `_collection` | Registries only grow ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L66-L86)[^ksa-modlibrary], [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L12-L55)[^ksa-serializedcollection]) |
| `GameSettings` | `private enum SettingsTab`, `_openTab` | Private nested enum ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L150-L160)[^ksa-gamesettings]) |
| `PhysicsStates` | `private static ComputeDrag` (in/ref parameters) | Called from workers ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090)[^ksa-physicsstates]) |
| `VehicleEditor` | private static `QuantizeScale`, `ScaleBoundsFor`, `ForEachPartWithSymmetry` | Reused by a replacement drag ([current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L4011-L4050)[^ksa-vehicleeditor]) |
| `GameSaves`, `VehicleSaves`, `XmlHelper` | get-only static serializers | Backing fields written via `UnsafeAccessor` ([current/decomp/KSA/XmlHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlHelper.cs#L15-L47)[^ksa-xmlhelper]) |

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): resolve handles once (static initialiser at `startup:immediate-load` or `startup:all-mods-loaded`); read and write on the main thread in `frame:draw-ui` or `editor:on-frame`; delegates
over physics helpers are called from vehicle workers in `physics:vehicle-solver`.

## Approaches

| # | Article | Mod | Form | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md) | KSA-DeltaVMap | Static field refs bound once; replay stock's writes | `frame:draw-ui` | No Harmony; must mirror stock's bookkeeping |
| 2 | [Plain GetField on internal registries and a generic holder for instance fields](call-private-game-members-through-reflection-and-delegates-2.md) | unscience (Parts Now) | Plain `GetField` on registries; generic holder for `_collection` | `startup:all-mods-loaded` | Simple; reflection per call is fine only when rare |
| 3 | [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md) | KSA-AdvancedFlightComputer | Typed `FieldRefAccess`, boxed private enum, by-ref lookups | `startup:immediate-load`, `frame:draw-ui` | Fast; needs validation of every handle |
| 4 | [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md) | KSAero | `MethodDelegate` mirroring `in`/`ref` | `physics:vehicle-solver` | Worker-safe; delegate runs the patched body if you patch the method |
| 5 | [Delegates over private editor helpers reused in a replacement drag](call-private-game-members-through-reflection-and-delegates-5.md) | unscience (Don't Stifle Me) | Delegates over private editor helpers | `editor:on-frame` | Reuses game logic; null-check on bind |
| 6 | [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md) | KittenExtensions | `UnsafeAccessor` on `k__BackingField` with `UnsafeAccessorType` | `startup:starmap-before-main` | Overwrites initonly statics; compiler-generated name |
| 7 | [Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md) | ShaderExtensions | The same injector copied into a consumer mod | `startup:starmap-before-main` | No hard dependency; code duplicates and drifts |

## See also

- [Validating patch targets and detecting game-update breakage](../../safety/validate-patch-targets-and-detect-game-update-breakage/overview.md) for the validation layer around these handles
- [Patching private and overloaded game methods by name](../patch-a-private-game-method-by-name/overview.md) for attaching *patches* to private methods
- [Changing behaviour of a game method marked AggressiveInlining](../patch-around-aggressive-inlining/overview.md) for why a resolved private method can still go silent
- Related tasks: add-plan-type-to-the-transfer-planner, load-and-unload-part-mods-at-runtime, replace-the-stock-aerodynamic-force-model, scale-a-vessel-or-its-parts-at-runtime, redirect-game-xml-loading, register-custom-xml-asset-type
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Add plan types to the stock Transfer Planner](../../../flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/overview.md) - related topic
- [Loading, validating and unloading part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md) - related topic
- [Replace the stock aerodynamic force model and measure the active one](../../../physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/overview.md) - related topic
- [Scaling a vessel or its parts at runtime](../../../vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/overview.md) - related topic
- [Redirect the game to patched copies of its XML and coordinate edits between mods](../../../assets/xml-patching/redirect-game-xml-loading/overview.md) - related topic
- [Register a custom XML asset type or inject new child elements into game XML](../../../assets/asset-xml/register-custom-xml-asset-type/overview.md) - related topic
- [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../../shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md) - related topic

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-serializedcollection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-xmlhelper]: KSA.XmlHelper (decompiled, build 2026.10.10.5554)
