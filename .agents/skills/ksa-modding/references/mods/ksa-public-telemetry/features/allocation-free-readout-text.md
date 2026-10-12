---
type: Mod Feature
title: Allocation-free formatted readout text
description: Formats every per-frame readout into stackalloc'd char spans with TryFormat and hands the spans straight to draw-list text calls, which Brutal's ImString encodes into a per-frame arena instead of allocating strings
mod: ksa-public-telemetry
category: ui
techniques: [imgui-drawlist, span-formatting]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImString
  - Brutal.ImGuiApi.ImDrawListExtensions
  - Brutal.ImGuiApi.ImGui
  - Brutal.Strings.ArenaStringBuilder
  - KSA.ImGuiBackend
  - KSA.Program
tags: [imgui, text, formatting, span, stackalloc, performance, draw-list]
---

Every readout on the bar is redrawn every frame: speeds, altitudes, pressures, forces and the
`HH:MM:SS` mission clock. The mod never builds a `string` for any of them. Each panel formats its
numbers into a stack buffer and passes the resulting `ReadOnlySpan<char>` straight to the draw-list
text calls, and Brutal's ImGui binding encodes those spans into a per-frame arena. The game's own FPS
counter uses the same pattern.

## How it works

1. **Stack buffer per draw.** `ReadoutPanel.Draw` takes `Span<char> buffer = stackalloc char[64]` once
   and reuses it for every capsule; `MissionClockPanel` uses `stackalloc char[16]`
   ([Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs#L71-L91), [Rendering/MissionClockPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/MissionClockPanel.cs#L47-L48)).
2. **Format into the span.** `Format.Write` calls `double.TryFormat(buffer, out written, format,
   CultureInfo.InvariantCulture)`, copies an optional unit suffix after the digits, and returns
   `buffer[..written]`; on failure it returns the literal `"--"`. `Speed`, `Distance`, `Mass`,
   `Pressure` and `Force` pick the unit by magnitude (m/s or km/s, m or km, Pa/kPa/MPa, N/kN/MN)
   ([Rendering/Format.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Format.cs#L7-L60)).[^kpt-format]
3. **Hand-rolled clock.** `Format.MissionTime` writes the two-digit hours, minutes and seconds
   character by character, falls back to `TryFormat` only for 100 hours or more, and formats the
   magnitude only, because the sign belongs to the `T-`/`T+` prefix the panel draws separately
   ([Rendering/Format.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Format.cs#L62-L106)).
4. **Spans into ImGui.** `Gfx.Text` passes the span to `ImDrawListPtr.AddText`, and the measuring
   helpers pass spans to `ImGui.CalcTextSize` ([Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L305-L323)). Both Brutal
   methods take an `ImString` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L211-L220),
   [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8092)), and `ImString` has an implicit conversion from
   `ReadOnlySpan<char>` that UTF-8 encodes the text into `ImString.SharedStorage`, an 8 MiB
   `ArenaStringBuilder` ([current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L79-L97),
   [current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L258-L263)).[^imgui-imdrawlistextensions] [^imgui-imgui] [^imgui-imstring]
5. **Arena reset every frame.** `ImGuiBackend.NewFrame` calls `ImString.SharedStorage.Reset()`, and
   `Program.PrepareImGui` calls it right before `ImGui.NewFrame`
   ([current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L56-L61), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139)), so the
   encoded bytes cost no managed allocation and are reclaimed wholesale each frame.[^ksa-imguibackend]
6. **Same as the game.** `Program.DrawFps` builds its "FPS" and "MS" lines in a
   `stackalloc char[128]` with a `SpanBuilder` and draws them through `ImGuiHelper.DrawText`
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3284)).[^ksa-program]
7. **Constant labels skip the encoding.** Fixed widget text in the settings UI uses UTF-8 literals
   (`"Overlay is still starting up."u8`), which convert to `ImString` without touching the arena
   ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L54-L64)).

## Lifecycle and timing

- **Formatted and drawn in one call, in `frame:draw-ui`.** The sampler stores plain numbers in the
  snapshot during `[StarMapAfterOnFrame]`; text exists only inside the panel draw, from
  `[StarMapAfterGui]` or the `DrawFps` postfix, between `ImGuiBackend.NewFrame` and `ImGui.Render`.
- **Arena lifetime is one ImGui frame.** An `ImString` made from a span points into
  `SharedStorage`, which the next `PrepareImGui` resets. `AddText` and `CalcTextSize` consume the bytes
  during the call, so nothing has to outlive it; an `ImString` cached in a field, or built in
  `[StarMapAfterOnFrame]` and drawn next frame, would point at reused memory.
- **`stackalloc` cannot escape.** The buffers live in the panel's `Draw` frame, which is why
  formatting and drawing happen in the same method rather than in the sampler.

## Key code

| Path | Role |
|---|---|
| [Rendering/Format.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Format.cs#L7-L106) | Span formatting with unit suffixes and the mission clock |
| [Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs#L192-L206) | Value formatting per readout kind |
| [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs#L305-L353) | Span text, measuring and font push around each call |

## Snippets

```csharp
// Rendering/Format.cs L7-L22
    private static ReadOnlySpan<char> Write(
        Span<char> buffer, double value, ReadOnlySpan<char> numericFormat, ReadOnlySpan<char> suffix)
    {
        if (!value.TryFormat(buffer, out int written, numericFormat, CultureInfo.InvariantCulture))
        {
            return "--".AsSpan();
        }

        if (suffix.Length > 0 && written + suffix.Length <= buffer.Length)
        {
            suffix.CopyTo(buffer[written..]);
            written += suffix.Length;
        }

        return buffer[..written];
    }
```

```csharp
// Rendering/ReadoutPanel.cs L71, L89-L91
        Span<char> buffer = stackalloc char[64];
// ...
            ReadOnlySpan<char> value = Unavailable(kind, context.Snapshot)
                ? "--".AsSpan()
                : ValueFor(kind, _smoothed[i], buffer);
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImString(ReadOnlySpan<char>)`, implicit conversion, `SharedStorage` | [current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L79-L97) | Span text into UTF-8 bytes in the arena |
| `ArenaStringBuilder.Append(ReadOnlySpan<char>)` | [current/decomp/Brutal.Strings/ArenaStringBuilder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Strings/ArenaStringBuilder.cs#L61-L67) | The arena append behind the conversion |
| `ImDrawListExtensions.AddText(in float2, ImColor8, ImString)` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L211-L220) | Draw-list text |
| `ImGui.CalcTextSize(ImString, ...)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8092) | Measuring for layout and tabular digits |
| `ImGuiBackend.NewFrame` | [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L56-L61) | Resets the arena once per frame |
| `Program.PrepareImGui`, `Program.DrawFps` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3284) | Where the reset happens; the game's own span-built text |

## Pitfalls and version notes

- **Invariant culture.** `"N0"`/`"N1"` with `CultureInfo.InvariantCulture` always group thousands with
  a comma and use a dot decimal, regardless of the player's locale; that suits a broadcast look but is
  not localised.
- **Every conversion copies.** Measuring and then drawing the same span encodes it twice, and the
  tabular-digit helper measures and draws one glyph at a time, so a clock string is encoded once per
  character per call. The arena makes this cheap, not free.
- **Plain `string` arguments are encoded too.** The implicit `string` conversion goes through the same
  span path, so a `string` passed every frame still costs an arena copy plus whatever allocated the
  string; `u8` literals avoid both for constant text.
- **Buffer size is the format's contract.** `Write` returns `"--"` when `TryFormat` runs out of room,
  and `MissionTime` needs at least 12 characters; size the `stackalloc` for the longest value plus the
  suffix.

## See also

- [Montserrat fonts in the ImGui atlas](montserrat-fonts-in-imgui-atlas.md) (tabular digits and the font push around each text call)
- [Broadcast overlay on the ImGui foreground draw list](foreground-drawlist-broadcast-overlay.md)
- [Mission clock and liftoff detection](mission-clock-and-liftoff-detection.md)
- [Live flight telemetry sampling](live-flight-telemetry-sampling.md)

[^kpt-format]: Rendering/Format.cs
[^imgui-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^imgui-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^imgui-imstring]: Brutal.ImGuiApi.ImString (decompiled, build 2026.10.10.5554)
[^ksa-imguibackend]: KSA.ImGuiBackend (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
