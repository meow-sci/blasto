---
type: Mod Feature
title: Entries in the stock orbit right-click menu
description: A transpiler on BurnContextMenu.Draw inserts a submenu before the popup's final EndPopup and appends entries to the "At Periapsis" and "At Apoapsis" submenus, found by reading the UTF-8 label literals from the IL
mod: ksa-advancedflightcomputer
category: ui
techniques: [harmony-transpiler, imgui-menu]
hooks: [harmony-transpiler KSA.BurnContextMenu.Draw]
lifecycle: [frame:draw-ui]
game_types: [KSA.BurnContextMenu, KSA.TransferPlanner, Brutal.ImGuiApi.ImGui]
tags: [context-menu, imgui, transpiler, il, utf8-literal, transfer-planner]
---

Right-click the controlled vehicle's orbit and the stock context menu shows an **Advanced Flight
Computer** submenu with the four quick-tools, and the stock **At Periapsis** / **At Apoapsis**
submenus gain **AFC: Set Apoapsis...** / **AFC: Set Periapsis...**. The entries open the Transfer
Planner with the type preselected; they do not place a burn at the clicked point, because each tool
computes its own burn time from user input.

## How it works

1. **Why a transpiler.** `BurnContextMenu.Draw()` opens an ImGui popup and closes it with
   `ImGui.EndPopup()` before returning; a postfix would run after the popup is closed, and ImGui items
   must be emitted between `BeginPopup` and `EndPopup` ([current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L259-L347)).
2. **Main group.** Earlier `EndPopup` calls belong to early-return branches, so the transpiler takes
   the **last** `call ImGui.EndPopup()` and inserts `call BurnMenuLauncher.DrawInline()` before it.
   `DrawInline` draws a separator and an `ImGui.BeginMenu("Advanced Flight Computer")` with four
   `MenuItem`s, balancing `EndMenu` in a `finally`.
3. **Find a submenu by its label.** Stock writes `ImGui.BeginMenu("At Periapsis"u8)`. A C# `u8`
   literal compiles to an `ldsflda` of a static data field in `<PrivateImplementationDetails>` followed
   by the length. `ReadMenuHeading` walks back up to 8 instructions from each `BeginMenu(ImString, bool)`
   call, finds the `ldsflda`, reads the bytes with `RuntimeHelpers.CreateSpan<byte>(field.FieldHandle)`
   and decodes them with the length from the following `ldc.i4` ([AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs#L136-L189)).
4. **Find the submenu end.** From the matching `BeginMenu`, count nested `BeginMenu`/`EndMenu` pairs
   to the `EndMenu` that closes it, and insert `ldc.i4 <submenu>; call BurnMenuLauncher.DrawApsisEntry(int)`
   before it. Insertions are applied at descending indices so earlier anchors do not move, and each
   uses [`TranspilerInsertion.MoveEntryMarkers`](plan-window-transpiler-injection.md) so jumps to the
   anchor run the new code.
5. **Opposite apse.** A burn at periapsis changes the apoapsis, so the periapsis submenu offers Set
   Apoapsis and vice versa.
6. **Opening the planner.** `OpenPlanner` refuses while stock's porkchop task runs (only stock's own
   body polls that worker) or a multi-pass execution is active, then sets `TransferPlanner.ShowPlanWindow = true`,
   the source vehicle and the type through `StockPlanner.SelectType`, and resets the quick-tool inputs
   ([AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs#L89-L128)).
7. **Optional.** If patching fails, the shortcuts are disabled and the quick-tools remain in the
   planner dropdown ([AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/ManeuverTools.cs#L73-L94)).

## Lifecycle and timing

`BurnContextMenu.Draw()` is called from `Program.OnDrawUiThreadSafe` when the editor is closed
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3229-L3233)), main thread, `frame:draw-ui`. The inserted calls run inside the
open popup. Opening the planner only changes static planner state; the planner window itself draws
later in the same `OnDrawUiThreadSafe` call.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs) | Transpiler, label reading, nested-menu matching |
| [AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs) | Menu items and `OpenPlanner` |
| [tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/BurnMenuLauncherTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/ManeuverTools/BurnMenuLauncherTest.cs) | Headless test of the injection |

## Snippets

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L160-L183
    /// <summary>Reads the UTF-8 literal passed to a <c>BeginMenu</c> call.</summary>
    private static string? ReadMenuHeading(List<CodeInstruction> code, int beginMenuIndex)
    {
        int stop = Math.Max(0, beginMenuIndex - MaxIlGapLabelToBeginMenu);
        for (int i = beginMenuIndex - 1; i >= stop; i--)
        {
            if (code[i].opcode != OpCodes.Ldsflda || code[i].operand is not FieldInfo data)
                continue;
            try
            {
                ReadOnlySpan<byte> bytes = RuntimeHelpers.CreateSpan<byte>(data.FieldHandle);
                int length = i + 1 < code.Count && TryReadInt32(code[i + 1], out int stated)
                             && stated >= 0 && stated <= bytes.Length
                    ? stated
                    : bytes.TrimEnd((byte)0).Length;
                return Encoding.UTF8.GetString(bytes[..length]);
            }
            catch (Exception)
            {
                // This static field does not contain a readable literal.
            }
        }
        return null;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `BurnContextMenu.Draw()` ([current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L259)) | Transpiled popup |
| `ImGui.EndPopup`, `BeginMenu(ImString, bool)`, `EndMenu`, `MenuItem` (Brutal.ImGuiApi) | Anchors and items |
| `TransferPlanner.ShowPlanWindow` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs)) | Open the planner |

## Pitfalls and version notes

- Match submenus by label, not by index: a new stock submenu would shift indices and move AFC's
  entries into the wrong menu.
- `u8` literals and their IL shape are a compiler detail; the reader falls back to trimming trailing
  zero bytes when no length follows.
- Use the last `EndPopup`, not the first, when a method has early-return branches that close the
  popup.

## See also

- [Plan window transpiler injection](plan-window-transpiler-injection.md)
- [Transfer Planner custom plan types](transfer-planner-custom-plan-types.md)
- [Apse and inclination quick tools](apse-and-inclination-quick-tools.md)
- [Anchor on a UTF-8 label literal in the method](../../../howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-3.md) - how-to article demonstrating this feature
- [Add shortcuts to the orbit right-click menu with a transpiler anchored on UTF-8 label literals](../../../howto/ui/menus/inject-ui-into-a-stock-window/inject-ui-into-a-stock-window-2.md) - how-to article demonstrating this feature

