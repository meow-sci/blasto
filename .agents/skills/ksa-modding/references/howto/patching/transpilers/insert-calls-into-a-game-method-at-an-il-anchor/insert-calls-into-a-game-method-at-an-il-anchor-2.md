---
type: How-To
title: Leave labels on the anchor so a jump skips the addition
description: Insert one call before PopWidgetStyle on the Mods settings page deliberately without taking the anchor's labels, so that when stock jumps over the page the added sections are skipped too; contrast with moving labels and say how to decide
category: patching
group: transpilers
topic: insert-calls-into-a-game-method-at-an-il-anchor
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/mods-settings-page-sections]
techniques: [harmony-transpiler, il-labels, registration-api]
hooks: [harmony-transpiler KSA.GameSettings.OnDrawUi]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.GameSettings, KSA.ConsoleStyle]
tags: [harmony, transpiler, il, insert, labels, settings, branch-target]
---

AFC puts its AUTOSTAGE and AUTO REMOVE settings directly on the game's own Settings > Mods page, under the stock list of installed mods, instead of opening a separate window. The mechanism is a single `call`
inserted by a transpiler, plus a list of section delegates that features register. Unlike the plan-window insertion ([Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md)), this one *keeps* the anchor's labels on the anchor, on purpose: stock jumps to the
anchor when the Mods tab is not open, and the added sections should be skipped on that path.

Prerequisites: a StarMap mod with `0Harmony` and the recipe from [Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md). Demonstrated by AFC in
[Sections on the stock Mods settings page](../../../../mods/ksa-advancedflightcomputer/features/mods-settings-page-sections.md).

## Background: how the game does it

Every settings tab renders into one body child that `GameSettings.OnDrawUi` closes with a single `ConsoleStyle.PopWidgetStyle()` call right after the Mods tab block:

```csharp
// current/decomp/KSA/GameSettings.cs L2208-L2216
if (_openTab == SettingsTab.Mods)
{
	ConsoleWidgets.RegionHeader(_settingsTabs.GetName(SettingsTab.Mods).AsSpan());
	List<ModEntry> mods = _temporary.Manifest.Mods;
	Span<char> buffer4 = stackalloc char[128];
	for (int i = 0; i < mods.Count; i++)
	{
		ModEntry modEntry = mods[i];
		ConsoleWidgets.BeginRow(modEntry.Id.AsSpan());
```

Source: [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2208-L2216)[^ksa-gamesettings]

```csharp
// current/decomp/KSA/GameSettings.cs L2247-L2253
		ConsoleWidgets.Checkbox(spanBuilder5, ref modEntry.Enabled, modEntry.Enabled != modEntry.State);
		ConsoleWidgets.EndRow();
	}
}
ConsoleStyle.PopWidgetStyle();
ConsoleStyle.EndBody();
DrawFooter();
```

Source: [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2247-L2253)

The IL: when `_openTab == SettingsTab.Mods` (the enum value `3`) is false, `brfalse IL_306a` jumps over the whole block to the `PopWidgetStyle` call, which therefore carries a label:

```text
// KSA.dll IL via ilspycmd -il: KSA.GameSettings::OnDrawUi (parameter lists shortened)
IL_2e5e: ldsfld valuetype KSA.GameSettings/SettingsTab KSA.GameSettings::_openTab
IL_2e63: ldc.i4.3
IL_2e64: ceq
IL_2e66: stloc.s 63
IL_2e68: ldloc.s 63
IL_2e6a: brfalse IL_306a
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

```text
// KSA.dll IL via ilspycmd -il: KSA.GameSettings::OnDrawUi (parameter lists shortened)
IL_3069: nop

IL_306a: call void KSA.ConsoleStyle::PopWidgetStyle(...)
IL_306f: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)

`GameSettings.OnDrawUi(Camera)` is called from `Program.OnDrawUiFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3141)[^ksa-program]).

## Lifecycle and timing

`OnDrawUiFrame` runs only while the HUD is drawn (`DrawUI`), on the main thread, so the sections run in `frame:draw-ui`, between `ImGui.NewFrame` and `ImGui.Render`, inside the settings window's
ImGui context. Code drawn at the anchor lands inside the body child with the stock widget style still pushed, so stock `ConsoleWidgets` rows render with the right fonts and column widths. The
transpiler runs once at patch time (`startup:all-mods-loaded`). Nothing in the simulation should be mutated beyond the mod's own settings objects. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Find the first call to the anchor.** `ConsoleStyle.PopWidgetStyle` is resolved with an empty parameter list; `FindAnchor` returns the first call index (or -1):

```csharp
// AdvancedFlightComputer/Core/ModSettingsPage.cs L12-L18, L59-L69
[HarmonyPatch(typeof(GameSettings), nameof(GameSettings.OnDrawUi), new[] { typeof(Camera) })]
internal static class ModSettingsPage
{
    private static readonly List<Action> _sections = new();

    private static readonly MethodInfo? Anchor =
        AccessTools.Method(typeof(ConsoleStyle), nameof(ConsoleStyle.PopWidgetStyle), Type.EmptyTypes);
// ...
    private static int FindAnchor(List<CodeInstruction> codes)
    {
        if (Anchor == null)
            return -1;
        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].Calls(Anchor))
                return i;
        }
        return -1;
    }
```

Source: [AdvancedFlightComputer/Core/ModSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ModSettingsPage.cs#L12-L69)[^ksa-advancedflightcomputer-modsettingspage]

2. **Insert without moving labels.** The new `call ModSettingsPage.DrawSections` is inserted at the anchor's index and the anchor keeps its label, so a branch that jumped to `PopWidgetStyle` still lands on
   `PopWidgetStyle` and the inserted call is skipped; only the fall-through path runs it:

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

Source: [AdvancedFlightComputer/Core/ModSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ModSettingsPage.cs#L42-L57)

   The result is the opposite of [Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md)'s label move. A local test against Lib.Harmony 2.4.2 shows the effect on a method with a branch to the anchor: with labels left on the anchor and the jump taken,
   only `Anchor()` ran; with labels moved, `Inserted()` ran first.

3. **Add a belt-and-braces runtime check.** `DrawSections` returns unless the private static `GameSettings._openTab` equals the `Mods` member of the private `SettingsTab` enum. The enum cannot be named
   from mod code, so `GameReflection` resolves the field once and parses `"Mods"` into a boxed enum value; the check is a boxed `Equals`:

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

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L266-L275)[^ksa-advancedflightcomputer-gamereflection]

```csharp
// AdvancedFlightComputer/Core/ModSettingsPage.cs L71-L90
private static bool IsModsPageOpen()
    => GameReflection.GameSettings_openTab_Mods is { } mods
       && mods.Equals(GameReflection.GameSettings_openTab!.GetValue(null));

public static void DrawSections()
{
    if (!IsModsPageOpen())
        return;
    foreach (Action section in _sections)
    {
        try
        {
            section();
        }
        catch (Exception ex)
        {
            string owner = section.Method.DeclaringType?.Name ?? section.Method.Name;
            LogHelper.WarnOnce($"settings-section:{owner}:{ex.GetType().Name}", $"[AFC] Settings section {owner} failed to draw: {ex}");
        }
    }
```

Source: [AdvancedFlightComputer/Core/ModSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ModSettingsPage.cs#L71-L90)

4. **Register sections instead of hard-coding them.** Features call `ModSettingsPage.Register(DrawSection)` in their patch block and `Unregister` in `Disable`; each section runs in its own `try`/`catch` that
   logs once per owner and exception type. A section uses the stock widgets so it looks native:

```csharp
// AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs L36-L52
internal static void DrawSection()
{
    ConsoleWidgets.Rule();
    ConsoleWidgets.RegionHeader("AUTO REMOVE FINISHED BURNS".AsSpan());

    bool enabled = AutoRemoveConfig.Enabled;
    if (ConsoleUi.CheckboxRow("ENABLED".AsSpan(), "AfcAutoRemoveEnabled".AsSpan(), ref enabled))
    {
        AutoRemoveConfig.Enabled = enabled;
        AutoRemoveConfig.Save();
    }

    ImGui.TextWrapped(
        "The game removes a finished or stopped auto-burn by itself when another burn follows it. When on, " +
        "the last finished burn is removed as well, for engine and RCS burns alike. Manual burns, and a " +
        "last burn that stopped without propellant, stay in the plan.");
}
```

Source: [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs#L36-L52)[^ksa-advancedflightcomputer-autoremovefeature]

5. **Give the page its own patch owner.** The page is its own `SettingsPage` block in the feature-gated patch sets, so a settings-window change in a game update costs only the sections.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/ModSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ModSettingsPage.cs) | Transpiler, registry, `DrawSections` |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs#L36-L52) | A registered section |

## Variations and gotchas

- **How to decide labels-move versus labels-stay.** If a jump to the anchor means "carry on with the normal flow", move the labels so your call runs on every path ([Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md)). If a jump means "skip this
  whole region", leave them so your addition is skipped with the region.
- **Insert before the anchor, do not replace it:** the page must still pop its style and end its body.
- **`IsAnchorPresent` uses `PatchProcessor.GetOriginalInstructions`** so the presence check works before patching and is covered by a headless test.
- **Two mods that both transpile this method with the same anchor will both draw;** the order follows Harmony patch order.
- **Settings drawn here are not part of the game's own temporary/apply flow;** AFC applies changes immediately and persists its own TOML files.

## See also

- [Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md), [Snapshot state before a stock call, restore it in a paired prefix](insert-calls-into-a-game-method-at-an-il-anchor-3.md), [Insert before the first and after the last instance of a pass boundary](insert-calls-into-a-game-method-at-an-il-anchor-4.md)
- [Inserting calls into a game method at an IL anchor](overview.md)
- [Sections on the stock Mods settings page](../../../../mods/ksa-advancedflightcomputer/features/mods-settings-page-sections.md)
- Related tasks: add-items-to-the-game-menu-bar, inject-ui-into-a-stock-window

[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-modsettingspage]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/ModSettingsPage.cs
[^ksa-advancedflightcomputer-gamereflection]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/GameReflection.cs
[^ksa-advancedflightcomputer-autoremovefeature]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs
