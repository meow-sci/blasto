---
type: How-To
title: Same-signature static replacement, operand-only edit
description: Replace one call in a big game method with a static method that has exactly the same parameter list and return type so only the instruction's operand changes, with the replacement free to call the original through a delegate or directly
category: patching
group: transpilers
topic: swap-one-call-inside-a-game-method-with-a-transpiler
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection
techniques: [harmony-transpiler, call-site-rewrite, method-delegate]
hooks:
  - harmony-transpiler KSA.PhysicsStates.ComputeDerivatives
  - harmony-transpiler KSA.TransferPlanner.DrawPlanWindow
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver, frame:draw-ui]
game_types: [KSA.PhysicsStates, KSA.TransferPlanner, KSA.Burn]
tags: [harmony, transpiler, call-site, same-signature, operand, replacement]
---

The simplest way to make a big game method call your code instead of one helper, without copying the method, is to give your code the
helper's exact signature and change only the `call` instruction's operand. Nothing is pushed or popped differently, so the evaluation stack is
untouched. KSAero does this for `ComputeDrag` inside `ComputeDerivatives`; KSA-AdvancedFlightComputer does it for `Burn.Create` inside the
Transfer Planner's `DrawPlanWindow`, additionally carrying labels and exception blocks onto the new instruction.

Prerequisites: a StarMap mod with `0Harmony` and a transpiler ([Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md)). Demonstrated in
[ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md) and [Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md).

## Background: how the game does it

Both targets are static methods called from a larger method. `PhysicsStates.ComputeDrag` takes `in` and `ref` structs by reference; in the IL
every `in`/`ref` parameter is a managed pointer, which is why a replacement must repeat them exactly. `Burn.Create` is a plain static factory:

```csharp
// current/decomp/KSA/PhysicsStates.cs L1089-L1090
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void ComputeDrag(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props, in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density, double dragCdA, double surfaceArea, double dt)
```

Source: [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090)[^ksa-physicsstates]

```csharp
// current/decomp/KSA/Burn.cs L185-L188
public static Burn Create(OrbitPointCce point, double time, double3 deltaVVlf, PatchedConic patch, Vehicle vehicle)
{
	return new Burn(point, time, deltaVVlf, patch, vehicle);
}
```

Source: [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L185-L188)[^ksa-burn]

The IL of the `Burn.Create` call in `TransferPlanner.DrawPlanWindow` (build 2026.10.10.5554): five arguments are on the stack when the `call` executes
(`OrbitPointCce`, `double`, `double3`, `PatchedConic`, `Vehicle`), and `stsfld _transferBurn` consumes the result. It is the only `Burn::Create` call in
that method; `CreateInterstellarBurns`, a separate method, makes its own:

```text
// KSA.dll IL via ilspycmd -il: KSA.TransferPlanner::DrawPlanWindow (parameter lists shortened)
IL_15d3: ldsfld class KSA.OrbitalTransfers/PorkChopEntry KSA.TransferPlanner::_selectedEntry
IL_15d8: ldfld class KSA.OrbitalTransfers/TransferData KSA.OrbitalTransfers/PorkChopEntry::TransferData
IL_15dd: ldfld valuetype [Brutal.Core.Numerics]Brutal.Numerics.double3 KSA.OrbitalTransfers/TransferData::TransferDvVlf
IL_15e2: ldloc.s 144
IL_15e4: call class KSA.Vehicle KSA.TransferPlanner::get_Source(...)
IL_15e9: call class KSA.Burn KSA.Burn::Create(...)
IL_15ee: stsfld class KSA.Burn KSA.TransferPlanner::_transferBurn
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

## Lifecycle and timing

- **Install: `startup:all-mods-loaded`.** KSAero and AFC patch from `[StarMapAllModsLoaded]`. A swapped call is installed while no worker can be
  executing the method (`ComputeDerivatives` runs only on vehicle workers once a universe exists).
- **Run: where the host method runs.** The swapped `ComputeDrag` call runs in `physics:vehicle-solver`; the swapped `Burn.Create` runs inside
  `DrawPlanWindow` in `frame:draw-ui` on the main thread, exactly when stock would create its burn, after which stock queues the returned burn into
  `InputEvents.BurnUpdateBuffer` as usual. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the replacement with the original's exact signature.** Same parameter count, order and types, including `in`/`ref`/`out` modifiers, and the
   same return type, and `static` (or the instance as the first parameter for an instance call). KSAero's replacement mirrors `ComputeDrag`:

```csharp
// KSAero/AirForcePatch.cs L99-L101
internal static void ComputeAirForces(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
    in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
    double dragCdA, double surfaceArea, double dt)
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L99-L101)[^ksaero-airforcepatch]

   AFC's mirrors `Burn.Create` ("Keep the `Burn.Create` signature so the stack arguments remain valid"):

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs L41-L50
// Keep the Burn.Create signature so the stack arguments remain valid.
public static Burn CreateMaybeMultiPass(
    OrbitPointCce point, double time, double3 deltaVVlf,
    PatchedConic patch, Vehicle vehicle)
{
    try
    {
        if (!HohmannMultiPassUI.Enabled && !HohmannFlybyUI.Enabled)
            return Burn.Create(point, time, deltaVVlf, patch, vehicle);

```

Source: [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs#L41-L50)[^ksa-advancedflightcomputer-hohmanncreateinterceptor]

2. **Find the call and edit only the operand.** `instruction.operand = replacement` keeps the opcode (`call`), labels and exception blocks. This is the
   whole change in KSAero ([Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md)); the match is qualified by what the call's arguments load
   ([Scan backwards through the argument loads to the previous call](../identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-1.md)).

3. **When the replacement is a new instruction, carry labels and blocks.** AFC replaces `Burn.Create` with a *new* `call` instruction and copies the
   original's labels and exception blocks onto it, so branches that targeted the call still land and exception regions stay intact:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L152-L163
int burnCreateIndex = code.FindIndex(instruction => instruction.Calls(burnCreate));
int createButtonIndex = FindCreateButton(
    code, burnCreateIndex, clickGate, out int gapTooLarge);

bool burnCreateReplaced = burnCreateIndex >= 0;
if (burnCreateReplaced)
{
    var swap = new CodeInstruction(OpCodes.Call, replacement);
    swap.labels.AddRange(code[burnCreateIndex].labels);
    swap.blocks.AddRange(code[burnCreateIndex].blocks);
    code[burnCreateIndex] = swap;
}
```

Source: [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L152-L163)[^ksa-advancedflightcomputer-planwindowpatchpipeline]

4. **Let the replacement call the original when it has nothing to add.** Inside `CreateMaybeMultiPass`, `Burn.Create(...)` is a direct call: the
   transpiler rewrote `DrawPlanWindow` only, so the replacement's own call is not rewritten. For a private or `AggressiveInlining` original, KSAero binds
   a delegate with the same parameter modifiers and calls through it, so the stock body runs unmodified:

```csharp
// KSAero/AirForcePatch.cs L50-L57
private static ComputeDragFn? _stockDrag;

internal static void Apply(Harmony harmony)
{
    if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
        throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

    _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L50-L57)

   See [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](../../internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-4.md) for the delegate type.

5. **Count and refuse.** Throw unless exactly one call was rewritten. AFC reports it through a once-only warning instead and leaves that stage of
   the window unmodified.

## Complete example

| File | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs) | Transpiler, replacement and stock-delegate fallback |
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L131-L171) | `InterceptCreate` swap |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs#L41-L70) | Same-signature replacement with fallback to stock |

## Variations and gotchas

- **The signature must be equal, not merely compatible.** A different parameter type, a missing `in`, or a different return type leaves the rewritten
  method with an evaluation stack that no longer matches what the surrounding instructions expect.
- **`FindIndex` swaps the first match.** In the analysed build `DrawPlanWindow` has one direct `Burn.Create` call; if stock adds another create path earlier in
  the method the swap would silently land on it. Qualify the match or count.
- **Selectivity.** Patching the callee would change every caller (ocean drag, other create paths); rewriting one call site changes one.
- **Inlining.** A call-site rewrite works whether or not the JIT inlined the callee ([Rewrite the one caller's call site instead of the inlined callee](../../internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-1.md)).

## See also

- [Wider signature with an extra ldarg.0 appended](swap-one-call-inside-a-game-method-with-a-transpiler-2.md), [Replacement that does mod work, then calls the original](swap-one-call-inside-a-game-method-with-a-transpiler-3.md), [Gate helper that skips the call for the mod's own condition](swap-one-call-inside-a-game-method-with-a-transpiler-4.md), [Adapter that fixes up arguments and converts the result back](swap-one-call-inside-a-game-method-with-a-transpiler-5.md)
- [Swapping one call inside a game method with a transpiler](overview.md)
- [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md), [Inject UI and swap calls inside the stock plan window with anchored transpilers](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md)
- [Writing a Harmony transpiler for a KSA method](../write-a-harmony-transpiler/overview.md)
- Related task: replace-the-stock-aerodynamic-force-model

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-burn]: KSA.Burn (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksa-advancedflightcomputer-hohmanncreateinterceptor]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs
[^ksa-advancedflightcomputer-planwindowpatchpipeline]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs
