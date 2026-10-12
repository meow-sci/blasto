---
type: How-To
title: Take over what CREATE does in the Transfer Planner with a same-signature call swap and a click gate
description: Insert controls before DrawCorrectionTransfer and PopWidgetStyle in TransferPlanner.DrawPlanWindow, replace the Burn.Create call with a static method of identical signature and filter the CREATE click with an injected bool-to-bool call
category: ui
group: menus
topic: inject-ui-into-a-stock-window
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection]
techniques: [harmony-transpiler]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow, starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.Burn, KSA.ConsoleStyle, KSA.ConsoleWidgets]
tags: [transfer-planner, plan-window, call-site-swap, click-gate, il-anchor, transpiler]
---
The Transfer Planning window shows porkchop results and a stock CREATE button that queues one burn. AFC adds its own flyby and multi-pass controls inside that window and makes CREATE queue its own burn (or first pass of several) instead, without replacing the window. A transpiler on `TransferPlanner.DrawPlanWindow` does three things: insert a control section before the transfer controls, insert the same call as a per-frame fallback, and swap the `Burn.Create` call for a method with an identical signature while gating the click. Prerequisites: StarMap, Harmony, the transpiler basics from [Swap one call inside a game method with a transpiler](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md). Demonstrated by [Plan window transpiler injection](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md).

## Background: how the game does it

`TransferPlanner.DrawPlanWindow(IGameViewport)` is one long method ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L305)[^ksa-ksa-transferplanner]). The pieces AFC anchors on, in method order:

| Anchor | Where | Role |
|---|---|---|
| `DrawCorrectionTransfer()` (private static) | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L616-L626) | Stock transfer controls; the first insertion point |
| `ConsoleStyle.PopWidgetStyle()` | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L726-L732) | Runs every frame the body is drawn; fallback insertion point |
| `ConsoleWidgets.PrimaryButton("CREATE")` | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L744-L752) | The create click, its `bool` result goes into a local |
| `Burn.Create(OrbitPointCce, double, double3, PatchedConic, Vehicle)` | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L774-L789) | The stock burn; the only direct call in this method (interstellar goes through `CreateInterstellarBurns`) |

`Burn.Create` is a public static method with that five-parameter signature ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L185));[^ksa-ksa-burn] stock then queues the returned burn through `InputEvents.BurnUpdateBuffer`. `DrawPlanWindow` is called from `Program.OnDrawUiThreadSafe` when `TransferPlanner.ShowPlanWindow` is true ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3234-L3237)).[^ksa-ksa-program]

## Lifecycle and timing

- **Patch at `startup:all-mods-loaded`**, again whenever Harmony re-patches the method (log lines use "once" helpers for that reason).
- **Inserted code runs at `frame:draw-ui`**, main thread, inside the open console window with its widget style pushed. `CreateMaybeMultiPass` runs exactly when stock would create its burn, so the returned burn is queued by stock as usual.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Attach a transpiler that delegates to a pipeline.** Keep the transpiler thin so the passes are testable:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L35-L41
    internal static IEnumerable<CodeInstruction> Rewrite(
        IEnumerable<CodeInstruction> instructions)
    {
        IEnumerable<CodeInstruction> rewritten = InjectCalculatedControls(instructions);
        rewritten = InjectFallbackControls(rewritten);
        return InterceptCreate(rewritten);
    }
```

   (`Patch_DrawPlanWindow` carries `[HarmonyPatch(typeof(TransferPlanner), nameof(TransferPlanner.DrawPlanWindow), new[] { typeof(IGameViewport) })]` and a `[HarmonyTranspiler]` that calls `PlanWindowPatchPipeline.Rewrite`; see [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs#L16) and [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs#L70-L73).)

2. **Insert your section before the stock controls.** Find the first call to the private method (resolved with `AccessTools.Method(typeof(TransferPlanner), "DrawCorrectionTransfer", Type.EmptyTypes)`), move the anchor's labels and begin-exception markers to your new instruction so branches that target the anchor run your call, and insert:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L61-L73
        int index = code.FindIndex(instruction => instruction.Calls(anchor));
        if (index < 0)
        {
            LogHelper.WarnOnce("transpiler-hohmann-multipass-no-call",
                $"[AFC] HohmannMultiPass transpiler: no call to DrawCorrectionTransfer " +
                $"found in DrawPlanWindow IL ({code.Count} IL instructions scanned); " +
                "multi-pass section not injected.");
            return code;
        }

        var injected = new CodeInstruction(OpCodes.Call, injectTarget);
        TranspilerInsertion.MoveEntryMarkers(code[index], injected);
        code.Insert(index, injected);
```

   `MoveEntryMarkers` is the shared helper (see approach 2 for its text). Note the contrast with approach 1: there labels are deliberately left on the anchor.

3. **Add the per-frame fallback and guard against double draw.** The same call is inserted before `PopWidgetStyle` for frames where the first anchor is not reached. The injected `DrawInline` remembers the ImGui frame count so the second call in a frame returns, "the first injection wins":

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs L69-L79
    private static int _lastFrameDrawn = -1;

    // Both window injection sites share one ImGui frame claim to prevent duplicate controls.
    public static void DrawInline()
    {
        if (!Enabled) return;
        int frame = ImGui.GetFrameCount();
        if (frame == _lastFrameDrawn) return;
        _lastFrameDrawn = frame;

        if (TryDrawActiveFastPath()) return;
```

4. **Swap the `Burn.Create` call.** Replace the operand of the `call` with a static method of **identical** signature, so the five arguments already on the evaluation stack stay valid; carry over the original instruction's labels and exception blocks:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L134-L167
        MethodInfo? burnCreate = AccessTools.Method(
            typeof(Burn), nameof(Burn.Create), BurnCreateSignature);
        MethodInfo? replacement = AccessTools.Method(
            typeof(HohmannCreateInterceptor),
            nameof(HohmannCreateInterceptor.CreateMaybeMultiPass));
        MethodInfo? clickGate = AccessTools.Method(
            typeof(HohmannCreateInterceptor),
            nameof(HohmannCreateInterceptor.ShouldAllowCreateClick));

        var code = new List<CodeInstruction>(instructions);
        if (burnCreate == null || replacement == null)
        {
            LogHelper.WarnOnce("transpiler-create-interceptor-noburncreate",
                "[AFC] HohmannCreateInterceptor transpiler: Burn.Create or its " +
                "replacement not found; leaving this DrawPlanWindow stage unmodified.");
            return code;
        }

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

        bool gateInjected = createButtonIndex >= 0 && clickGate != null;
        if (gateInjected)
            code.Insert(createButtonIndex + 1, new CodeInstruction(OpCodes.Call, clickGate));
```

   The replacement has to fall back to stock on any problem; AFC's calls `Burn.Create` itself when its features are disabled or an exception is thrown:

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs L41-L49
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

5. **Gate the click.** Walk backwards from the `Burn.Create` call to the nearest `ConsoleWidgets.PrimaryButton` call (at most 160 instructions) and insert a `bool -> bool` call right after it. The button's result is on the stack, so the injected method can veto the click without touching anything else:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs L173-L200
    private static int FindCreateButton(
        List<CodeInstruction> code, int burnCreateIndex,
        MethodInfo? clickGate, out int gapTooLarge)
    {
        gapTooLarge = 0;
        if (clickGate == null || burnCreateIndex < 0)
            return -1;

        for (int i = burnCreateIndex - 1; i >= 0; i--)
        {
            if (!IsPrimaryButtonCall(code[i]))
                continue;

            int gap = burnCreateIndex - i;
            if (gap <= MaxIlGapPrimaryButtonToBurnCreate)
                return i;

            gapTooLarge = gap;
            break;
        }
        return -1;
    }

    private static bool IsPrimaryButtonCall(CodeInstruction instruction)
        => (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
           && instruction.operand is MethodInfo method
           && method.DeclaringType == typeof(ConsoleWidgets)
           && method.Name == nameof(ConsoleWidgets.PrimaryButton);
```

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs L17-L31
    public static bool ShouldAllowCreateClick(bool wasClicked)
    {
        if (!wasClicked) return false;
        if (!HohmannMultiPassUI.Enabled && !HohmannFlybyUI.Enabled) return true;
        try
        {
            if (StockPlanner.SourceVehicle is not Vehicle source) return true;
            if (!MultiPassRegistry.Has(source.Id)) return true;

            TimedAlert.Create(ActiveExecAlert, Color.Yellow, 5.0);
            DefaultCategory.Log.Warning(
                $"[AFC] HohmannCreateInterceptor: Create click absorbed for " +
                $"vehicle={source.Id} (active multi-pass exec running; cancel it first).");
            return false;
        }
```

6. **Gate your own features on the anchors.** AFC enables the multi-pass UI only if `HasCalculatedControlsAnchor` is true ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L160-L174)), so an injected section never exists without its host. A headless test that runs the transpiler over `PatchProcessor.GetOriginalInstructions(DrawPlanWindow)` and asserts the four insertions is how the mod catches game updates.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowPatchPipeline.cs#L10-L231) | The three passes and anchor probes |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.cs#L70-L73) | Transpiler entry on the patch class |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs#L11-L70) | Same-signature `Burn.Create` replacement and click gate |
| [AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs#L69-L79) | `DrawInline` once-per-frame guard |
| [AdvancedFlightComputer/Core/TranspilerInsertion.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/TranspilerInsertion.cs#L5-L20) | Label and exception-marker hand-over |

## Variations and gotchas

- **A call swap needs the exact parameter list and return type** (static, or the instance as first parameter for an instance call).
- **Bound the backwards search.** A refactor that moves the button far away should disable the gate instead of gating the wrong button.
- **`FindIndex` swaps the first `Burn.Create` call** in the method. In build 2026.10.10.5554 `DrawPlanWindow` has one direct call; if stock adds another create path earlier in the method, the swap would silently land on it. Probe the count.
- **Harmony re-runs transpilers** whenever another patch is added or removed on the method, so keep passes pure and log with once-helpers.
- **Other plan types** (new transfer algorithms shown in the dropdown) take a different route; see [Add a plan type to the Transfer Planner](../../../flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/overview.md). AFC also uses a prefix on `DrawPlanWindow` for its own types.
- **Stock selection state.** Opening the planner from elsewhere must set `TransferPlanner.ShowPlanWindow` rather than flipping private flags; see approach 2.

## See also

- [Overview](overview.md)
- [Approach 1: Settings > Mods page sections](inject-ui-into-a-stock-window-1.md)
- [Approach 2: orbit context menu shortcuts](inject-ui-into-a-stock-window-2.md)
- [Swap one call inside a game method with a transpiler](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md)
- [Add a plan type to the Transfer Planner](../../../flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/overview.md)
- [Create and read maneuver nodes](../../../flight-and-orbits/autopilot/create-and-read-maneuver-nodes/overview.md)
- Mod feature: [Plan window transpiler injection](../../../../mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection.md)

[^ksa-ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-ksa-burn]: KSA.Burn (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
