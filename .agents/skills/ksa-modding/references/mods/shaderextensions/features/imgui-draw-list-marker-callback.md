---
type: Mod Feature
title: Tagging an ImGui window with a draw-list marker callback
description: A no-op ImDrawList callback whose copied user data carries a marker hash and a shader key lets any mod tag the current ImGui window for a custom shader without referencing ShaderExtensions; the renderer later scans each draw list's commands for that marker
mod: shaderextensions
category: ui
techniques: [imgui-drawlist, inter-mod-api, native-interop]
hooks: [imgui ImDrawList.AddCallback (consumer), harmony-transpiler KSA.Program.RenderGame]
lifecycle: [frame:draw-ui, frame:render, render:imgui]
game_types:
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawCmd
  - Brutal.ImGuiApi.ImDrawListExtensions
  - Brutal.ImGuiApi.ImGuiPlatformIOPtr
  - Brutal.ImGuiApi.ImGuiViewportPtr
  - KSA.ImGuiBackendVulkanImpl
  - KSA.KeyHash
tags: [imgui, draw-list, callback, inter-mod, shaders]
---

ImGui's final draw data has no notion of "windows", only a list of `ImDrawList`s, one per window. To
let a mod say "run shader X on this window", ShaderExtensions uses the one per-window channel that
survives into the draw data: a draw-list callback command. The consumer adds a callback whose user
data is two `uint`s, a fixed marker (`KeyHash.Make("SxImGuiShader").Code`) and the `Id` hash of an
`<ImGuiShader>` asset. The callback itself does nothing; ShaderExtensions reads the payload while
rendering. The README ships the helper for consumers to copy; KSA_AircraftHUD carries it verbatim
apart from the pointer cast and calls it for its HUD window
([ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L37-L46),
[AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1021)).[^sx-readme] [^hud-helpers] [^hud-main]

## How it works

1. **Tag (consumer).** Between `ImGui.Begin` and `ImGui.End`, `SxImGui.CustomShader(key)` calls
   `ImGui.GetWindowDrawList().AddCallback(DummyCallback, &data, 8)`. Brutal's
   `ImDrawListExtensions.AddCallback(ImDrawListPtr, ImDrawCallback, Ptr, nuint)` converts the
   delegate with `Marshal.GetFunctionPointerForDelegate` and passes the size to ImGui, which copies the
   eight bytes into the draw list's `_CallbacksDataBuf`
   ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L416-L419),
   [current/decomp/Brutal.ImGuiApi/ImDrawListPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListPtr.cs#L39)).[^brutal-imdrawlistext] [^brutal-imdrawlistptr]
2. **Scan (ShaderExtensions).** `ImGuiRenderers.Render` walks `ImGui.GetPlatformIO().Viewports`, then
   each viewport's `DrawData.CmdLists`. `FindCustomRenderer` iterates the list's `CmdBuffer`, using
   `_CallbacksDataBuf.Count` as a budget so it stops once all callback payloads have been seen, and
   `IsCustomRenderer` accepts a command with a non-null `UserCallback`, `UserCallbackDataSize >= 8`
   and a first `uint` equal to the marker; the second `uint` is the shader key
   ([ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L68-L117),
   [current/decomp/Brutal.ImGuiApi/ImDrawCmd.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawCmd.cs#L25-L31)).[^sx-imguirenderers] [^brutal-imdrawcmd]
3. **Consume.** A tagged list is post-processed and then replaced in place by a single image draw,
   which also removes the callback (see [Per-window ImGui post shader](imgui-window-post-shader.md)).
4. **Untagged path.** If a list is not processed (ShaderExtensions missing, vehicle editor), the
   game's `RenderDrawData` sees the callback, turns the pointer back into a delegate with
   `Marshal.GetDelegateForFunctionPointer` (cached per pointer), calls it and resets its render state,
   so the window draws normally
   ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L774-L791)).[^ksa-imguibackendvulkan]

## Lifecycle and timing

- **Tag:** `frame:draw-ui`, any point where ImGui calls are legal (between `ImGui.NewFrame` and
  `ImGui.Render`), for example from `[StarMapAfterGui]`.
- **Scan:** `frame:render`, inside `ImGuiPreRender`, which the RenderGame transpiler runs in place of
  the swapchain `BeginRenderPass`. `ImGui.Render()` has already finalized the draw data
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2286)); the game's ImGui pass
  draws it a few calls later.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md#L135-L169) | Helper class and usage for consumers |
| [ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L16-L17) | Marker hash and minimum payload size |
| [ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L91-L117) | Command scan |
| [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L37-L46) | A consumer's copy of the helper |

## Snippets

```csharp
// README.md L144-L153
internal static class SxImGui
{
  internal static readonly KeyHash MarkerKey = KeyHash.Make("SxImGuiShader");
  internal static unsafe void CustomShader(KeyHash key)
  {
    var data = new uint2(MarkerKey.Code, key.Code);
    ImGui.GetWindowDrawList().AddCallback(DummyCallback, (nint)(&data), ByteSize.Of<uint2>().Bytes);
  }
  private static unsafe void DummyCallback(ImDrawList* parent_list, ImDrawCmd* cmd) { }
}
```

```csharp
// ShaderExtensions/ImGuiShader/ImGuiRenderers.cs L91-L117
        private static unsafe uint? FindCustomRenderer(ImDrawListPtr drawList)
        {
            var remData = drawList._CallbacksDataBuf.Count;

            for (var i = 0; i < drawList.CmdBuffer.Count && remData > 0; i++)
            {
                if (IsCustomRenderer(ref drawList.CmdBuffer.Data[i], out uint index))
                    return index;
                remData -= drawList.CmdBuffer.Data[i].UserCallbackDataSize;
            }
            return null;
        }

        private static unsafe bool IsCustomRenderer(ref ImDrawCmd cmd, out uint index)
        {
            index = 0xFFFFFFFF;
            if (cmd.UserCallback == IntPtr.Zero)
                return false;
            if (cmd.UserCallbackDataSize < MinMarkerSize)
                return false;

            var data = *(uint2*)cmd.UserCallbackData;
            if (data[0] != MarkerHash)
                return false;
            index = data[1];
            return true;
        }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImDrawListExtensions.AddCallback(ImDrawCallback, Ptr, nuint)` and the `delegate* unmanaged[Cdecl]` overload | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L416-L424) | Adding the marker command |
| `ImDrawListPtr._CallbacksDataBuf`, `CmdBuffer` | [current/decomp/Brutal.ImGuiApi/ImDrawListPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListPtr.cs#L39) | Payload storage and command list |
| `ImDrawCmd.UserCallback`, `UserCallbackData`, `UserCallbackDataSize` | [current/decomp/Brutal.ImGuiApi/ImDrawCmd.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawCmd.cs#L25-L31) | Marker test |
| `ImGuiPlatformIOPtr.Viewports` | [current/decomp/Brutal.ImGuiApi/ImGuiPlatformIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiPlatformIOPtr.cs#L87) | Every viewport's draw data |
| `ImGuiViewportPtr.DrawData`, `Flags`, `ID`, `Size` | [current/decomp/Brutal.ImGuiApi/ImGuiViewportPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiViewportPtr.cs#L12-L30) | Per-viewport lists, minimized check, identity |
| `ImGuiBackendVulkanImpl.RenderDrawData` user-callback branch | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L774-L791) | What happens to an unprocessed marker |

## Pitfalls and version notes

- **Keep the delegate alive.** The README helper passes a method group, so a new delegate is created
  on every call and nothing references it after `AddCallback` returns, while ImGui keeps only the
  native function pointer. If the list is not consumed (editor, ShaderExtensions absent) the game
  calls that pointer later in the frame; .NET's interop rules require the delegate to stay alive
  until then. Store the delegate in a `static readonly` field, or use the
  `delegate* unmanaged[Cdecl]` overload with an `[UnmanagedCallersOnly]` method.
- **One shader per window, top-level only.** Only the first marker in a list counts, and child windows
  have their own draw lists, so their content is drawn on top of the shaded parent (README
  limitation).
- **Key collisions.** The key is a 32-bit hash of the asset `Id`; an unknown key makes
  `ImGuiShaderReference.AllShaders.Get` return `null`, which then throws when the renderer list is
  created.
- **The marker is ImGui-version sensitive.** It depends on ImGui copying callback user data
  (`_CallbacksDataBuf`), available in the ImGui build that Brutal binds in 5554.

## See also

- [Per-window ImGui post shader](imgui-window-post-shader.md)
- [RenderGame transpiler around the swapchain render pass](rendergame-render-pass-transpiler.md)
- [Attribute duck typing as an inter-mod API](duck-typed-attribute-api.md)
- [Bend the picture: how the host replays a tagged ImGui window offscreen and rewrites it to one image](../../../howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^hud-helpers]: KSA_AircraftHUD/ShaderExtensionsHelpers.cs
[^hud-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^brutal-imdrawlistext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imdrawlistptr]: Brutal.ImGuiApi.ImDrawListPtr (decompiled, build 2026.10.10.5554)
[^sx-imguirenderers]: ShaderExtensions/ImGuiShader/ImGuiRenderers.cs
[^brutal-imdrawcmd]: Brutal.ImGuiApi.ImDrawCmd (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkan]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
