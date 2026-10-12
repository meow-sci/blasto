---
type: How-To
title: Add sections to the Settings > Mods page with a one-call transpiler
description: Insert a single call before the only ConsoleStyle.PopWidgetStyle in GameSettings.OnDrawUi so registered section delegates draw inside the stock settings body, and run them only while the private Mods tab is open
category: ui
group: menus
topic: inject-ui-into-a-stock-window
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/mods-settings-page-sections]
techniques: [harmony-transpiler, reflection, imgui-window]
hooks:
  - harmony-transpiler KSA.GameSettings.OnDrawUi
  - reflection KSA.GameSettings._openTab
  - starmap StarMapAllModsLoaded
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.GameSettings, KSA.ConsoleStyle, KSA.ConsoleWidgets, KSA.Camera]
tags: [settings, mods-page, transpiler, il-anchor, private-enum, stock-window]
---
KSA's Settings window has a **Mods** tab that lists installed mods with enable checkboxes. KSA-AdvancedFlightComputer (AFC) adds its own settings blocks under that list instead of opening a separate window. The whole mechanism is one transpiler that inserts `call ModSettingsPage.DrawSections` before a unique anchor, plus a list of section delegates that features register. Because nothing stock is replaced, another mod can do the same. Prerequisites: StarMap, Harmony and a working knowledge of reading IL (see [Write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md)). Demonstrated by [Mods settings page sections](../../../../mods/ksa-advancedflightcomputer/features/mods-settings-page-sections.md).

## Background: how the game does it

`GameSettings.OnDrawUi(Camera)` draws the whole Settings window. Each tab is an `if (_openTab == SettingsTab.X)` block inside one body; the Mods tab is the last one, and the method then pops the widget style, ends the body, draws the footer and ends the window:

```csharp
// current/decomp/KSA/GameSettings.cs L2208-L2211, L2248-L2254
		if (_openTab == SettingsTab.Mods)
		{
			ConsoleWidgets.RegionHeader(_settingsTabs.GetName(SettingsTab.Mods).AsSpan());
			List<ModEntry> mods = _temporary.Manifest.Mods;
// ...
				ConsoleWidgets.EndRow();
			}
		}
		ConsoleStyle.PopWidgetStyle();
		ConsoleStyle.EndBody();
		DrawFooter();
		ConsoleStyle.EndWindow();
```

([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2208-L2254)[^ksa-ksa-gamesettings]). There is exactly one `ConsoleStyle.PopWidgetStyle()` call in the method, so code inserted immediately before it lands inside the body with the stock widget style still pushed. `_openTab` is a **private static** field of the **private** enum `SettingsTab` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L150-L158), [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1104)), so the open-tab test has to go through reflection. `OnDrawUi` is called from `Program.OnDrawUiFrame`, after `[StarMapBeforeGui]` has run ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3141)).[^ksa-ksa-program]

## Lifecycle and timing

- **Apply the patch at `startup:all-mods-loaded`** (`[StarMapAllModsLoaded]`); AFC applies it inside a validated, failure-isolated block of its own so that a settings-window change costs only the sections. The transpiler runs once, and again whenever Harmony re-patches the method.
- **Sections run at `frame:draw-ui`**, on the main thread, inside the open settings window, between `ImGui.NewFrame` and `ImGui.Render`, only while the HUD is drawn. They may call ImGui and `ConsoleWidgets`; they should not mutate the simulation.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the patch class and find the anchor.** Target `GameSettings.OnDrawUi(Camera)` by exact signature; find the first `call ConsoleStyle.PopWidgetStyle()`:

```csharp
// AdvancedFlightComputer/Core/ModSettingsPage.cs L12-L30
[HarmonyPatch(typeof(GameSettings), nameof(GameSettings.OnDrawUi), new[] { typeof(Camera) })]
internal static class ModSettingsPage
{
    private static readonly List<Action> _sections = new();

    private static readonly MethodInfo? Anchor =
        AccessTools.Method(typeof(ConsoleStyle), nameof(ConsoleStyle.PopWidgetStyle), Type.EmptyTypes);

    internal static bool IsAnchorPresent
    {
        get
        {
            MethodBase? target = AccessTools.Method(typeof(GameSettings), nameof(GameSettings.OnDrawUi), new[] { typeof(Camera) });
            return target != null && FindAnchor(PatchProcessor.GetOriginalInstructions(target)) >= 0;
        }
    }

    internal static void ApplyPatches(Harmony harmony)
        => harmony.CreateClassProcessor(typeof(ModSettingsPage)).Patch();
```

2. **Write the transpiler.** Insert before the anchor. Leave the anchor's labels where they are so any branch that jumps to `PopWidgetStyle` skips your sections rather than landing in the middle of your call. If the anchor is missing, log and return the instructions unchanged so the mod still loads:

```csharp
// AdvancedFlightComputer/Core/ModSettingsPage.cs L42-L69
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

3. **Draw only while the Mods tab is open.** Resolve the private field and the boxed `Mods` member of the private enum once, as static handles; the check is a boxed `Equals`:

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

```csharp
// AdvancedFlightComputer/Core/ModSettingsPage.cs L71-L91
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
    }
```

4. **Register a section.** A section is an `Action`. AFC's registrations are one line in each feature's patch step (and `Unregister` on disable). The registry is a plain list, so several features, or several mods using the same idea, can share the anchor:

```csharp
// AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs L15-L19
    internal static void ApplyPatches(Harmony harmony)
    {
        AutoRemoveConfig.Init();
        ModSettingsPage.Register(DrawSection);
    }
```

5. **Draw with stock widgets.** The widget style is still pushed, so `ConsoleWidgets` rows get the right fonts and column widths:

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

6. **Validate the anchor at load and isolate the patch.** AFC checks the reflection handles for the `SettingsPage` feature and applies the patch only if they exist, in its own patch owner:

```csharp
// AdvancedFlightComputer/Mod.cs L42-L44
        // Its own owner, so a settings-window rename costs the mod sections and nothing else.
        if (Validated("SettingsPage", GameReflection.ValidateSettingsPage))
            _patches.TryApply("SettingsPage", ModSettingsPage.ApplyPatches);
```

   Check the log for the warning from step 2 after a game update; no warning and no section means the Mods tab was not open.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/ModSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ModSettingsPage.cs#L9-L92) | Patch class, transpiler, registry, tab check |
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L266-L275) | `_openTab` and the boxed `Mods` member |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs#L15-L52) | Registration and the smallest section |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L30-L44) | Load-time validation and isolated apply |

## Variations and gotchas

- **Insert before, do not replace.** The page must still pop its style and end its body.
- **Anchor uniqueness is a game-version fact.** `PopWidgetStyle` occurs once in `OnDrawUi` in build 2026.10.10.5554; a new tab that pushes its own style would change that. Probe with `PatchProcessor.GetOriginalInstructions` before patching (AFC's `IsAnchorPresent`).
- **Private enum.** `SettingsTab` is private; renaming the `Mods` member or `_openTab` silently disables the sections (the handle resolves to null), which is why the check is a separate feature in AFC.
- **Two mods inserting at the same anchor** both draw, in Harmony patch order.
- **Settings drawn here are outside the game's own apply flow.** The stock page uses a temporary copy and footer; AFC applies its changes immediately and persists its own TOML. See [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md).
- **A lighter alternative** for just a toggle is a menu entry; see [Add a menu to the game's top menu bar](../add-items-to-the-game-menu-bar/overview.md).

## See also

- [Overview](overview.md)
- [Approach 2: orbit context menu](inject-ui-into-a-stock-window-2.md)
- [Insert calls into a game method at an IL anchor](../../../patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/overview.md)
- [Match the stock console UI style](../../windows/match-the-stock-console-ui-style/overview.md)
- Mod feature: [Mods settings page sections](../../../../mods/ksa-advancedflightcomputer/features/mods-settings-page-sections.md)

[^ksa-ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
