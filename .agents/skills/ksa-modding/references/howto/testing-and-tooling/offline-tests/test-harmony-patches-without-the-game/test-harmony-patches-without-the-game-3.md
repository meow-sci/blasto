---
type: How-To
title: Run transpilers over the game's real IL and scope test patches to a disposable Harmony owner
description: In a HeadlessHarness test, feed PatchProcessor.GetOriginalInstructions of the current game method to the mod's transpiler, assert on the resulting instructions, and apply hook sets under an IDisposable scope with their own Harmony id
category: testing-and-tooling
group: offline-tests
topic: test-harmony-patches-without-the-game
approach: 3
difficulty: advanced
prerequisites: [harmony, headlessharness]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/headless-harness-test-mod
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
techniques: [harmony-transpiler, headless-harness, il-tests]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [event:headless-test-run, startup:all-mods-loaded]
game_types: [KSA.TransferPlanner, KSA.FlightComputer, KSA.Vehicle]
tags: [testing, harmony, transpiler, il, headlessharness, patch-scope, api-drift]
---

Outcome: the mod's transpilers are tested against the IL of the game method as it exists in the build under test, and tests that need a feature's runtime hooks install them under a throwaway Harmony id and remove them afterwards, so a suite exercises real patch code without depending on the mod's own load state.
Demonstrated by KSA-AdvancedFlightComputer's `ManeuverTranspilerTest` ([tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs#L16-L47)). Prerequisites: the consumer-mod setup in [Test a mod against the real game with HeadlessHarness](../headless-test-harness-for-game-mods/headless-test-harness-for-game-mods-1.md).

## Background: how the game does it

AFC rewrites the transfer planner's plan window by IL injection: the target is `TransferPlanner.DrawPlanWindow(IGameViewport)` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L305)). A transpiler is a static method from `IEnumerable<CodeInstruction>` to the rewritten sequence, so it can be run on a list of instructions without installing anything: `PatchProcessor.GetOriginalInstructions(MethodBase)` returns the target's current IL. That makes an IL-level test cheap and exact, and it runs against the build you actually have.
The harness does not run the mod's `OnFullyLoaded`, so the production patches are not installed unless a test installs them.

## Lifecycle and timing

Tests run inside a headless harness session (`event:headless-test-run`), stepping the simulation through the session's driver. The production hooks they exercise attach at mod load (`startup:all-mods-loaded`) in the game; in the harness each test applies the ones it needs under its own owner and removes them in `finally` or `Dispose`. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Fetch the original IL of the current game method and run the transpiler by reflection.** Transpilers are usually private to the patch class; a two-line helper invokes one and materialises the result:

   ```csharp
   // tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs L229-L233
       private static List<CodeInstruction> Run(Type patch, IEnumerable<CodeInstruction> code)
       {
           MethodInfo transpiler = patch.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic)!;
           return new List<CodeInstruction>((IEnumerable<CodeInstruction>)transpiler.Invoke(null, [code])!);
       }
   ```

2. **Assert on the instruction stream.** Find calls with `CodeInstruction.Calls`, and assert what the pipeline inserted and where: here, that both multi-pass draw callbacks are inserted immediately before stock's own anchors, that the stock order (correction controls before the style pop) is intact, and that create interception is installed once:

   ```csharp
   // tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs L16-L47
   public sealed class ManeuverTranspilerTest : AfcTest
   {
       public override string Name => "afc-maneuver-transpilers";

       protected override void Execute(TestContext t)
       {
           MethodInfo draw = AccessTools.Method(typeof(TransferPlanner), nameof(TransferPlanner.DrawPlanWindow), [typeof(IGameViewport)]);
           List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(draw);
           List<CodeInstruction> patched = Run(typeof(Patch_DrawPlanWindow), original);
           MethodInfo inline = AccessTools.Method(typeof(HohmannMultiPassUI), nameof(HohmannMultiPassUI.DrawInline));
           MethodInfo correction = AccessTools.Method(typeof(TransferPlanner), "DrawCorrectionTransfer", Type.EmptyTypes);
           MethodInfo popStyle = AccessTools.Method(typeof(ConsoleStyle), nameof(ConsoleStyle.PopWidgetStyle), Type.EmptyTypes);
           int correctionIndex = patched.FindIndex(ins => ins.Calls(correction));
           int popStyleIndex = patched.FindIndex(ins => ins.Calls(popStyle));
           t.Check("one pipeline inserts both multi-pass draw callbacks",
               CountCalls(patched, inline) == 2
               && correctionIndex > 0 && patched[correctionIndex - 1].Calls(inline)
               && popStyleIndex > 0 && patched[popStyleIndex - 1].Calls(inline));

           // The first injection wins, so the correction controls must remain first.
           t.Check("stock draws the correction controls before it pops the widget style",
               correctionIndex > 0 && popStyleIndex > correctionIndex);

           MethodInfo replacement = AccessTools.Method(typeof(HohmannCreateInterceptor), nameof(HohmannCreateInterceptor.CreateMaybeMultiPass));
           MethodInfo gate = AccessTools.Method(typeof(HohmannCreateInterceptor), nameof(HohmannCreateInterceptor.ShouldAllowCreateClick));
           t.Check("the same pipeline installs create interception",
               CountCalls(patched, replacement) == 1 && CountCalls(patched, gate) == 1);

           CheckMenu(t);
           CheckExceptionRegions(t);
           CheckInstalledPatchCounts(t, draw);
       }
   ```

3. **Install real patches under a test-owned Harmony id and count what is installed.** Invoke the mod's patch routine with a fresh `Harmony` instance, read the result with `Harmony.GetPatchInfo` and filter by owner so other mods' patches on the same method do not matter; always `UnpatchAll` in `finally`:

   ```csharp
   // tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs L49-L85
       private static void CheckInstalledPatchCounts(TestContext t, MethodInfo draw)
       {
           const string id = "afc.tests.plan-window-patch-counts";
           var harmony = new Harmony(id);
           MethodInfo preRender = AccessTools.Method(
               typeof(TransferPlanner), nameof(TransferPlanner.OnPreRender), [typeof(IViewport)]);
           try
           {
               MethodInfo apply = typeof(AdvancedFlightComputer.Mod).GetMethod(
                   "PatchPlanWindow", BindingFlags.Static | BindingFlags.NonPublic)!;
               apply.Invoke(null, [harmony]);

               Patches? drawPatches = Harmony.GetPatchInfo(draw);
               t.Check("DrawPlanWindow has one patch of each kind",
                   CountOwned(drawPatches?.Prefixes, id) == 1
                   && CountOwned(drawPatches?.Transpilers, id) == 1
                   && CountOwned(drawPatches?.Postfixes, id) == 1);

               Patches? preRenderPatches = Harmony.GetPatchInfo(preRender);
               t.Check("OnPreRender has one AFC hook",
                   CountOwned(preRenderPatches?.Prefixes, id) == 0
                   && CountOwned(preRenderPatches?.Transpilers, id) == 0
                   && CountOwned(preRenderPatches?.Postfixes, id) == 1);

               MethodInfo selectedTransfer = AccessTools.Method(
                   typeof(TransferPlanner), "DrawSelectedTransfer", [typeof(IViewport)]);
               MethodInfo selectedTransferUi = AccessTools.Method(
                   typeof(TransferPlanner), "DrawSelectedTransferUi", [typeof(IGameViewport)]);
               t.Check("PlanWindow owns both flyby suppression hooks",
                   CountOwned(Harmony.GetPatchInfo(selectedTransfer)?.Prefixes, id) == 1
                   && CountOwned(Harmony.GetPatchInfo(selectedTransferUi)?.Prefixes, id) == 1);
           }
           finally
           {
               harmony.UnpatchAll(id);
           }
       }
   ```

4. **Wrap hook sets in an `IDisposable` scope.** The scope applies the shared hooks under its own owner, sets the config directly (so the suite never touches the player's mod directory), and restores everything on dispose, including when construction fails halfway:

   ```csharp
   // tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveTestPatches.cs L7-L41
   // The harness never runs Mod.OnFullyLoaded, so each burn-removal test applies the shared hooks on
   // its own owner, among them the FlightComputer.RaisePendingAlerts postfix a finished engine or RCS
   // burn arrives through, and removes them when it ends. The config is set directly rather than
   // loaded, so the suite never touches the player's mod directory.
   internal static class AutoRemoveTestPatches
   {
       public static Scope Apply() => new();

       internal sealed class Scope : IDisposable
       {
           private readonly Harmony _harmony = new("com.maxi.afc.harnesstests.autoremove");
           private readonly bool _enabledBefore = SharedVehicleHooks.AutoRemoveEnabled;

           internal Scope()
           {
               try
               {
                   SharedVehicleHooks.ApplyPatches(_harmony);
                   SharedVehicleHooks.AutoRemoveEnabled = true;
                   AutoRemoveConfig.Enabled = true;
               }
               catch
               {
                   Dispose();
                   throw;
               }
           }

           public void Dispose()
           {
               SharedVehicleHooks.AutoRemoveEnabled = _enabledBefore;
               AutoRemoveConfig.Reset();
               _harmony.UnpatchAll(_harmony.Id);
           }
       }
   ```

   A test then declares it with one line ([tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveBurnTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveBurnTest.cs#L58)): `using AutoRemoveTestPatches.Scope patches = AutoRemoveTestPatches.Apply();`.

5. **Let drift escape.** Do not catch `MissingMemberException` or `TypeLoadException` in a test: the harness classifies an escaping one as game-API drift. A separate reflection-targets test resolves every string-keyed handle and IL anchor against the running build, so a game rename fails there with a clear name ([approach 1](../headless-test-harness-for-game-mods/headless-test-harness-for-game-mods-1.md)).

6. **Mention the oracle companion.** DeltaVMap's suite uses the same harness to demand equality with stock private routines instead of testing patches ([the second HeadlessHarness approach](../headless-test-harness-for-game-mods/headless-test-harness-for-game-mods-2.md)).

## Complete example

| File | Role |
|---|---|
| [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/ManeuverTranspilerTest.cs) | IL tests over `GetOriginalInstructions` and patch-count tests |
| [tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveTestPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/AutoRemove/AutoRemoveTestPatches.cs) | Disposable scope with its own Harmony id |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs) | Drift detector for reflection handles and IL anchors |

## Variations and gotchas

- **Unique ids.** Use a distinct Harmony id per scope; `UnpatchAll(id)` removes only that owner's patches.
- **Compare by owner.** `Patches.Prefixes`, `Transpilers` and `Postfixes` carry `owner`; counting only your own makes the test immune to other mods loaded alongside.
- **Instruction equality is brittle.** Assert relationships (this call precedes that anchor) rather than whole sequences, so harmless game changes do not fail the suite.
- **Needs the real game.** Unlike approach 1, this requires the harness and the game DLLs; use it when the exact IL matters.
- **Game updates.** An IL test on a new build is the direct answer to "does my transpiler still find its anchors".

## See also

- [Overview](overview.md); [approach 1](test-harmony-patches-without-the-game-1.md), [approach 2](test-harmony-patches-without-the-game-2.md)
- [Test a mod against the real game with HeadlessHarness](../headless-test-harness-for-game-mods/overview.md), [Write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md)
- Mod features: [AFC headless harness test mod](../../../../mods/ksa-advancedflightcomputer/features/headless-harness-test-mod.md), [AFC validated reflection handles](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md)
