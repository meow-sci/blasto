---
type: How-To
title: Anchor on a UTF-8 label literal in the method
description: Find a stock submenu in BurnContextMenu.Draw by the UTF-8 label literal passed to ImGui.BeginMenu, by reading the bytes of the u8 literal's static data field, then find the matching EndMenu and insert before it
category: patching
group: transpilers
topic: identify-a-call-by-its-argument-il
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/orbit-context-menu-shortcuts]
techniques: [harmony-transpiler, il-anchor, utf8-literal]
hooks: [harmony-transpiler KSA.BurnContextMenu.Draw]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.BurnContextMenu, KSA.TransferPlanner]
tags: [harmony, transpiler, il, anchor, utf8-literal, imgui, menu]
---

The orbit context menu is built from several `ImGui.BeginMenu("...")` calls that are all the same method with different label literals. There is no distinctive call to anchor on, but the UI
text is distinctive. KSA-AdvancedFlightComputer finds the stock "At Periapsis" and "At Apoapsis" submenus by decoding the C# `u8` literal passed to each `BeginMenu`, finds the matching
`EndMenu` by counting nested pairs, and inserts its own entry before it. It also adds a main group before the last `EndPopup`.

Prerequisites: a StarMap mod with `0Harmony`, the recipe from [Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md), and the insertion discipline from
[Move labels and begin-exception markers onto the inserted instruction](../insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-1.md). Demonstrated by AFC in [Entries in the stock orbit right-click menu](../../../../mods/ksa-advancedflightcomputer/features/orbit-context-menu-shortcuts.md).

## Background: how the game does it

`BurnContextMenu.Draw()` opens an ImGui popup, draws submenus conditionally, and closes the popup with `ImGui.EndPopup()`. Early-return branches call `EndPopup` too, so the method has
several `EndPopup` calls and the last one closes the main path:

```csharp
// current/decomp/KSA/BurnContextMenu.cs L296-L330
}
CelestialPosition point = _burnMenuPoint.Value;
if (_burnMenuMode == BurnMenuMode.ParentDeparture)
{
	DrawParentDepartureAction(burnMenuVehicle, in point);
	ImGui.EndPopup();
	return;
}
if (point.Patch == null)
{
	ImGui.EndPopup();
	return;
}
DrawCountdownHeader(burnMenuVehicle, in point);
bool flag = false;
if (_burnMenuNearPeriapsis && ImGui.BeginMenu("At Periapsis"u8))
{
	CelestialPosition point2 = FeaturePoint(in point, _burnMenuPeriapsisTime.Value);
	DrawCountdownHeader(burnMenuVehicle, in point2);
	flag |= DrawBurnActions(burnMenuVehicle, in point2, node: false);
	ImGui.EndMenu();
}
if (_burnMenuNearApoapsis && ImGui.BeginMenu("At Apoapsis"u8))
{
	CelestialPosition point3 = FeaturePoint(in point, _burnMenuApoapsisTime.Value);
	DrawCountdownHeader(burnMenuVehicle, in point3);
	flag |= DrawBurnActions(burnMenuVehicle, in point3, node: false);
	ImGui.EndMenu();
}
if (_burnMenuNearAscending && ImGui.BeginMenu("At Ascending Node"u8))
{
	CelestialPosition point4 = FeaturePoint(in point, _burnMenuAscendingTime.Value);
	DrawCountdownHeader(burnMenuVehicle, in point4);
	flag |= DrawBurnActions(burnMenuVehicle, in point4, node: true);
	ImGui.EndMenu();
```

Source: [current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L296-L330)[^ksa-burncontextmenu]

A C# `u8` literal compiles to an `ldsflda` of a static data field in `<PrivateImplementationDetails>`, then the length, then a `ReadOnlySpan<byte>` constructor and an implicit conversion to
`ImString`. The IL for one `BeginMenu` call (the "At Periapsis" submenu, guarded by `_burnMenuNearPeriapsis`): the data field is five instructions before the `call`, and the length (12) is the next instruction:

```text
// KSA.dll IL via ilspycmd -il: KSA.BurnContextMenu::Draw (parameter lists shortened)
IL_0199: ldsfld bool KSA.BurnContextMenu::_burnMenuNearPeriapsis
IL_019e: brfalse.s IL_01b9

IL_01a0: ldsflda valuetype '<PrivateImplementationDetails>'/'__StaticArrayInitTypeSize=13' '<PrivateImplementationDetails>'::E6B5B5D01CA5303D0F8A5D925BB118ECCDD089B07854F44624470F83F4065166
IL_01a5: ldc.i4.s 12
IL_01a7: newobj instance void valuetype [System.Runtime]System.ReadOnlySpan`1<uint8>::.ctor(...)
IL_01ac: call valuetype [Brutal.ImGui]Brutal.ImGuiApi.ImString [Brutal.ImGui]Brutal.ImGuiApi.ImString::op_Implicit(...)
IL_01b1: ldc.i4.1
IL_01b2: call bool [Brutal.ImGui]Brutal.ImGuiApi.ImGui::BeginMenu(...)
IL_01b7: br.s IL_01ba
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

The method contains six `EndPopup` calls (`IL_0095`, `IL_0117`, `IL_013a`, `IL_0168`, `IL_0183`, and the last at `IL_0348`) and four `BeginMenu`/`EndMenu` pairs.

## Lifecycle and timing

`BurnContextMenu.Draw()` is called from `Program.OnDrawUiThreadSafe` when the editor is closed ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3229-L3233)[^ksa-program]), on the main thread in `frame:draw-ui`. The inserted calls run
inside the open popup. The transpiler runs at patch time (`startup:all-mods-loaded`). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take the last `EndPopup` for the main group.** Earlier `EndPopup` calls belong to early-return branches; inserting before the last one puts the call on the main path inside the popup:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L50-L57
// Earlier EndPopup calls belong to early returns. Insert before the final one.
int anchorIndex = -1;
for (int i = 0; i < code.Count; i++)
{
    if (!code[i].Calls(anchor))
        continue;
    anchorIndex = i;
}
```

Source: [AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs#L50-L57)[^ksa-advancedflightcomputer-patch-burncontextmenu-launcher]

2. **List the submenus you want by heading.** The headings are the anchors, not the positions, so a new stock submenu cannot shift the entries into the wrong menu:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L18-L26
// An apse burn changes the opposite apse. Match headings so a new submenu cannot move an entry.
private static readonly (string Heading, int Submenu)[] ApseSubmenus =
{
    ("At Periapsis", BurnMenuLauncher.PeriapsisSubmenu),
    ("At Apoapsis", BurnMenuLauncher.ApoapsisSubmenu),
};

// A menu label is loaded a few instructions ahead of the call that consumes it.
private const int MaxIlGapLabelToBeginMenu = 8;
```

Source: [AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs#L18-L26)

3. **Read the literal.** From each `BeginMenu(ImString, bool)` call walk back up to 8 instructions to the `ldsflda`, read the bytes with
   `RuntimeHelpers.CreateSpan<byte>(field.FieldHandle)` and decode them with the length from the following `ldc.i4` (falling back to trimming trailing zero bytes when no length
   follows):

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L160-L198
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

private static bool TryReadInt32(CodeInstruction instruction, out int value)
{
    value = 0;
    if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int wide)
        value = wide;
    else if (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte narrow)
        value = narrow;
    else if (instruction.opcode.Value >= OpCodes.Ldc_I4_0.Value
             && instruction.opcode.Value <= OpCodes.Ldc_I4_8.Value)
        value = instruction.opcode.Value - OpCodes.Ldc_I4_0.Value;
    else
        return false;
    return true;
}
```

Source: [AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs#L160-L198)

4. **Find the submenu by heading, then its end.** The match is `Calls(BeginMenu)` plus the decoded heading; the end is the `EndMenu` that closes it, counting nested pairs:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L130-L158
/// <summary>Finds the submenu with the specified heading.</summary>
private static int FindSubmenu(List<CodeInstruction> code, MethodInfo beginMenu, string heading)
{
    for (int i = 0; i < code.Count; i++)
    {
        if (code[i].Calls(beginMenu) && ReadMenuHeading(code, i) == heading)
            return i;
    }
    return -1;
}

/// <summary>Finds the matching end of a submenu, including nested menus.</summary>
private static int FindSubmenuEnd(List<CodeInstruction> code, MethodInfo beginMenu,
    MethodInfo endMenu, int beginIndex)
{
    int depth = 0;
    for (int i = beginIndex + 1; i < code.Count; i++)
    {
        if (code[i].Calls(beginMenu))
            depth++;
        else if (code[i].Calls(endMenu))
        {
            if (depth == 0)
                return i;
            depth--;
        }
    }
    return -1;
}
```

Source: [AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs#L130-L158)

5. **Insert at descending indices.** Collect the insertions, sort by end index descending and apply them so earlier anchors do not move; each insertion uses `MoveEntryMarkers` so jumps to
   the anchor run the new code:

```csharp
// AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs L95-L117
var insertions = new List<(int End, int Submenu)>();
foreach ((string heading, int submenu) in ApseSubmenus)
{
    int begin = FindSubmenu(code, beginMenu, heading);
    int end = begin < 0 ? -1 : FindSubmenuEnd(code, beginMenu, endMenu, begin);
    if (end < 0)
    {
        LogHelper.WarnOnce("transpiler-burn-menu-heading-" + heading,
            "[AFC] BurnMenuLauncher transpiler: no complete '" + heading + "' submenu " +
            "found in BurnContextMenu.Draw IL; that submenu gets no AFC entry.");
        continue;
    }
    insertions.Add((end, submenu));
}

// Insert at descending indices so earlier anchors do not move.
insertions.Sort((left, right) => right.End.CompareTo(left.End));
foreach ((int end, int submenu) in insertions)
{
    InsertBefore(code, end,
        new CodeInstruction(OpCodes.Ldc_I4, submenu),
        new CodeInstruction(OpCodes.Call, apsisTarget));
}
```

Source: [AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs#L95-L117)

6. **Fail soft.** If a heading is not found the transpiler warns once and that submenu gets no entry; if patching fails entirely the shortcuts are disabled and the quick-tools remain in the planner
   dropdown.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs) | Transpiler, label reader, submenu finder |
| [AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs#L89-L128)[^ksa-advancedflightcomputer-burnmenulauncher] | What the inserted calls do |

## Variations and gotchas

- **`u8` literals and their IL shape are a compiler detail.** The reader tolerates a missing length; a different compiler could use a different shape, in which case the heading lookup returns null and the
  entry is skipped.
- **Match by label, not by index.** A new stock submenu would shift indices.
- **Use the last `EndPopup`, not the first,** when early-return branches close the popup.
- **Bound the label gap.** `MaxIlGapLabelToBeginMenu = 8` keeps the walk from reading a label that belongs to another call.
- **A postfix cannot do this.** ImGui items must be emitted between `BeginPopup` and `EndPopup`; a postfix on `Draw` runs after the popup is closed.

## See also

- [Scan backwards through the argument loads to the previous call](identify-a-call-by-its-argument-il-1.md), [Anchor on a unique neighbouring call, then walk to the target](identify-a-call-by-its-argument-il-2.md), [Match the instruction that consumes the result](identify-a-call-by-its-argument-il-4.md)
- [Identifying the right call in a method that makes several similar calls](overview.md)
- [Entries in the stock orbit right-click menu](../../../../mods/ksa-advancedflightcomputer/features/orbit-context-menu-shortcuts.md)
- [Inserting calls into a game method at an IL anchor](../insert-calls-into-a-game-method-at-an-il-anchor/overview.md), [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md)
- Related topic: inject-ui-into-a-stock-window

[^ksa-burncontextmenu]: KSA.BurnContextMenu (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-patch-burncontextmenu-launcher]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/ManeuverTools/Patch_BurnContextMenu_Launcher.cs
[^ksa-advancedflightcomputer-burnmenulauncher]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs
