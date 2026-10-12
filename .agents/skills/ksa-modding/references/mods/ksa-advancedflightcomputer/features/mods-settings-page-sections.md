---
type: Mod Feature
title: Sections on the stock Mods settings page
description: A one-call transpiler on GameSettings.OnDrawUi inserts a section host before the page's PopWidgetStyle, and draws registered sections only while the private Mods tab is open
mod: ksa-advancedflightcomputer
category: ui
techniques: [harmony-transpiler, reflection, imgui-window]
hooks:
  - harmony-transpiler KSA.GameSettings.OnDrawUi
  - reflection KSA.GameSettings._openTab
  - reflection KSA.ModLibrary.AllParts
lifecycle: [frame:draw-ui]
game_types: [KSA.GameSettings, KSA.ConsoleStyle, KSA.ConsoleWidgets, KSA.ModLibrary]
tags: [settings, imgui, transpiler, gamesettings, consolewidgets]
---

AFC puts its AUTOSTAGE and AUTO REMOVE FINISHED BURNS settings directly on the game's own
Settings > Mods page, under the stock list of installed mods, instead of opening a separate window.
The mechanism is a single `call` inserted by a transpiler and a list of section delegates that
features register, so other features (or other mods using the same idea) can add their own block.

## How it works

1. **Anchor.** Every settings tab renders into one body child that `GameSettings.OnDrawUi` closes
   with a single `ConsoleStyle.PopWidgetStyle()` call, right after the Mods tab block
   ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2208-L2252)). AFC's transpiler finds the first call to that method
   and inserts `call ModSettingsPage.DrawSections` immediately before it
   ([AdvancedFlightComputer/Core/ModSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ModSettingsPage.cs#L42-L69)). Code drawn there lands inside
   the body child with the stock widget style still pushed, so stock `ConsoleWidgets` rows render
   with the right fonts and column widths.
2. **Labels stay on the anchor.** The new instruction is inserted without taking the anchor's
   labels, so any branch that jumps to `PopWidgetStyle` skips the sections rather than landing in the
   middle of them.
3. **Tab check.** `DrawSections` returns unless the private static `GameSettings._openTab` equals the
   `Mods` member of the private `SettingsTab` enum. `GameReflection` resolves the field once and
   parses `"Mods"` into a boxed enum value, so the check is a boxed `Equals`
   ([AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L266-L275)).
4. **Registration.** Features call `ModSettingsPage.Register(DrawSection)` in their patch block and
   `Unregister` in `Disable`; each section runs inside its own try/catch that logs once per owner and
   exception type (see [warn-once logging](warn-once-logging-and-diagnostics.md)).
5. **Sections.** `AutoRemoveFeature.DrawSection` draws `ConsoleWidgets.Rule()`,
   `ConsoleWidgets.RegionHeader(...)`, one checkbox row and wrapped help text, and saves its TOML on
   change ([AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs#L36-L52)). The
   AutoStage section adds delay tables over every part variant, read from `ModLibrary.AllParts`; that
   handle is a soft anchor, so the page still draws without the tables (see
   [staging delays](staging-delays.md)).
6. **Own patch owner.** The page is its own `SettingsPage` block in the
   [feature-gated patch sets](feature-gated-patch-sets.md), so a settings-window change in a game
   update costs only the sections.

## Lifecycle and timing

`GameSettings.OnDrawUi(Camera)` is called from `Program.OnDrawUiFrame` on the main thread
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3141)), which runs only while the HUD is drawn (`DrawUI`). The sections
therefore run in `frame:draw-ui`, between `ImGui.NewFrame` and `ImGui.Render`, inside the settings
window's ImGui context; ImGui calls are legal and nothing in the simulation should be mutated beyond
the mod's own settings objects.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Core/ModSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ModSettingsPage.cs) | Transpiler, anchor probe, section registry, tab check |
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L266-L280) | `_openTab`, boxed `Mods` member, `ModLibrary.AllParts` soft anchor |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs) | Smallest registered section |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageSettingsPage.cs) | Larger section with per-part delay tables |

## Snippets

```csharp
// AdvancedFlightComputer/Core/ModSettingsPage.cs L42-L57
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        int anchorIdx = FindAnchor(codes);
        if (anchorIdx < 0)
        {
            DefaultCategory.Log.Warning(
                $"[AFC] Settings page: no ConsoleStyle.PopWidgetStyle call in GameSettings.OnDrawUi ({codes.Count} instructions), mod sections not drawn.");
            return codes;
        }

        // Labels stay on the anchor, so a jump to it skips the sections instead of landing mid-call.
        codes.Insert(anchorIdx, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(ModSettingsPage), nameof(DrawSections))));
        return codes;
    }
```

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L266-L275
    // Which settings page the nav rail has open, read by the shared Mods settings section host.
    // The enum is private to GameSettings, so its Mods member is resolved as a boxed value once and compared by equality.
    // Its own feature, so a settings-window rename costs the sections only.
    [UsedBy(Feature.SettingsPage)]
    public static readonly FieldInfo? GameSettings_openTab =
        AccessTools.Field(typeof(GameSettings), "_openTab");

    [UsedBy(Feature.SettingsPage)]
    public static readonly object? GameSettings_openTab_Mods =
        GameSettings_openTab?.FieldType is { IsEnum: true } tab && Enum.TryParse(tab, "Mods", out object? mods) ? mods : null;
```

## Game APIs touched

| Member | Use |
|---|---|
| `GameSettings.OnDrawUi(Camera)` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1880)) | Transpiled host method |
| `GameSettings._openTab`, private enum `SettingsTab` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L150)) | Detect the Mods page |
| `ConsoleStyle.PopWidgetStyle()` ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L641)) | IL anchor |
| `ConsoleWidgets.Rule`, `RegionHeader`, `BeginRow`, `Checkbox`, `EndRow` ([current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs)) | Stock-styled section widgets |
| `ModLibrary.AllParts` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs)) | Part list for the AutoStage delay tables |

## Pitfalls and version notes

- Insert before the anchor, do not replace it: the page must still pop its style and end its body.
- `IsAnchorPresent` uses `PatchProcessor.GetOriginalInstructions` so the presence check works before
  patching and is covered by the headless `afc-reflection-targets` test.
- Settings drawn here are not part of the game's own `_temporary`/apply flow; AFC applies changes
  immediately and persists its own TOML files.
- Two mods that both transpile this method with the same anchor will both draw; the order follows
  Harmony patch order.

## See also

- [Stock-styled console UI](stock-styled-console-ui.md)
- [Staging delays](staging-delays.md)
- [Automatic removal of finished burns](auto-remove-finished-burns.md)
- [Validated reflection handles](validated-reflection-handles.md)
- [Leave labels on the anchor so a jump skips the addition](../../../howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-2.md) - how-to article demonstrating this feature
- [Add sections to the Settings > Mods page with a one-call transpiler](../../../howto/ui/menus/inject-ui-into-a-stock-window/inject-ui-into-a-stock-window-1.md) - how-to article demonstrating this feature

