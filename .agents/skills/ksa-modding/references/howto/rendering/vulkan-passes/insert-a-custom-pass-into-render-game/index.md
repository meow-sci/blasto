# Game Feature

* [Insert a custom Vulkan pass into KSA's frame](overview.md) - KSA records one frame as scene, resolve, bloom and composite stages, and a mod can hook a stage boundary with a Harmony postfix or prefix, or rewrite RenderGame, to record its own work where depth, colour and light are in the state it needs

# How-To

* [Postfix RenderTarget.ResolveAttachments for a post-resolve pass](insert-a-custom-pass-into-render-game-1.md) - Record a depth-aware pass into the resolved main colour image after the gizmo pass by postfixing the final RenderTarget.ResolveAttachments call
* [Postfix SuperMeshRenderSystem.RenderMainPass to draw inside the opaque scene pass](insert-a-custom-pass-into-render-game-2.md) - Record depth-tested draws into the already-open MSAA opaque scene pass with a postfix on SuperMeshRenderSystem.RenderMainPass
* [Postfix SuperMeshRenderSystem.RenderTranslucencyPass with tracked barriers](insert-a-custom-pass-into-render-game-3.md) - Reopen a dynamic-rendering scope over the scene attachments after atmosphere, clouds and plumes so translucent mod content composites last
* [Prefix SunbloomRenderer.Render to dispatch compute on the HDR scene](insert-a-custom-pass-into-render-game-4.md) - Hook the public SunbloomRenderer.Render method with a Harmony prefix to dispatch compute shaders on the HDR scene colour and depth just before bloom and the tonemap composite
* [Transpile Program.RenderGame around the swapchain render pass](insert-a-custom-pass-into-render-game-5.md) - Rewrite the BeginRenderPass and EndRenderPass call sites in Program.RenderGame so a mod can record dynamic-rendering passes and image copies before and after the swapchain pass
