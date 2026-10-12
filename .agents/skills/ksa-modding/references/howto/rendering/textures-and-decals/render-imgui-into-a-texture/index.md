# Game Feature

* [Render ImGui UI into a Vulkan texture for use in the 3D world](overview.md) - Run a second ImGui context and a second Vulkan ImGui backend on a mod-owned render pass so a whole UI becomes an image shown in the scene or in another window, or put a live camera feed on a part

# How-To

* [BindlessTextures.SetTexture on the monitor's material to a secondary viewport's colour image](render-imgui-into-a-texture-2.md) - Show a live camera feed on a monitor part by pointing its bindless diffuse and emissive slots at a secondary viewport's colour image, with a projected-quad ImGui fallback
* [ImGui rendered to a texture and drawn as a world-space quad](render-imgui-into-a-texture-3.md) - Draw the off-screen ImGui texture on a depth-tested premultiplied-alpha quad anchored to a part or the camera, and focus it by ray-picking so the UI is fully interactive in the world
* [Secondary ImGui context sharing the game's atlas, second backend, fence-ring submission](render-imgui-into-a-texture-1.md) - Create a second ImGui context that shares the main font atlas, a second Vulkan ImGui backend on a mod-owned render pass, and render a whole UI into an image every changed frame
