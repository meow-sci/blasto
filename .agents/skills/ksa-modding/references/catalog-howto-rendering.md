# Rendering

Drawing things the game does not draw by itself - ImGui overlays on the sky and world, custom Vulkan passes, textures, meshes, lights, particles, plumes and decals - plus the Vulkan resource discipline that keeps those additions alive across frames, resizes and shutdown.

## Vulkan passes

Recording your own Vulkan work inside KSA's frame: command submission, pass insertion, compatible pipelines, HDR compute passes, renderer rebuilds, resource teardown and frame capture.

### [Build a Vulkan pipeline compatible with KSA's scene render target](howto/rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md)

A pipeline recorded inside KSA's scene pass must match its colour and depth formats, sample count and dynamic-rendering setup, and offscreen work of the mod's own needs a standalone render-pass-less pipeline

- [Program.OffscreenTarget.SetupGraphicsPipeline, created lazily](howto/rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/build-a-vulkan-pipeline-for-the-scene-target-1.md) — advanced
- [Hand-built dynamic-rendering pipeline reusing UnlitMeshVert with premultiplied alpha](howto/rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/build-a-vulkan-pipeline-for-the-scene-target-2.md) — advanced
- [Private colour and depth targets with a standalone dynamic-rendering pipeline](howto/rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/build-a-vulkan-pipeline-for-the-scene-target-3.md) — advanced

### [Capture rendered frames from KSA](howto/rendering/vulkan-passes/capture-rendered-frames/overview.md)

Grab the finished scene for streaming or automated visual testing without stalling the GPU by recording a copy in the frame command buffer and reading it back when the staging slot is reused, and render part thumbnails at runtime with the game thumbnail pipeline

- [Transpiler before the final End, blit into per-slot staging buffers, readback on slot reuse](howto/rendering/vulkan-passes/capture-rendered-frames/capture-rendered-frames-1.md) — advanced
- [ThumbnailCreator framing plus ThumbnailDynamic per-frame submit, two parts per frame](howto/rendering/vulkan-passes/capture-rendered-frames/capture-rendered-frames-2.md) — advanced

### [Free mod-owned Vulkan resources without device loss](howto/rendering/vulkan-passes/free-vulkan-resources-safely/overview.md)

The GPU may still read an image or buffer for several frames after the mod stops referencing it, so stop recording first, free after frames in flight or WaitIdle, and never touch the destroyed device at game exit

- [Stop recording now, free two frames later after WaitIdle; nullable images; no device work in unload](howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-1.md) — advanced
- [Retire by MaxFramesInFlight + 1 frames](howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-2.md) — intermediate
- [Publish new GPU resources after a fence, retire old ones after WaitIdle](howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-3.md) — advanced

### [Recreate mod render targets when KSA rebuilds its renderer](howto/rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)

KSA recreates swapchain-sized targets on resize, MSAA changes and high-resolution screenshots, so mod pipelines and images tied to those extents must be rebuilt at the same point

- [Postfix Program.RebuildRenderer plus a first-frame schedule and per-frame extent check](howto/rendering/vulkan-passes/handle-renderer-rebuilds/handle-renderer-rebuilds-1.md) — advanced
- [Rebuild targets in place on a live size change](howto/rendering/vulkan-passes/handle-renderer-rebuilds/handle-renderer-rebuilds-2.md) — advanced
- [Re-create the private material pipeline on the renderer-rebuild event](howto/rendering/vulkan-passes/handle-renderer-rebuilds/handle-renderer-rebuilds-3.md) — advanced

### [Insert a custom Vulkan pass into KSA's frame](howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/overview.md)

KSA records one frame as scene, resolve, bloom and composite stages, and a mod can hook a stage boundary with a Harmony postfix or prefix, or rewrite RenderGame, to record its own work where depth, colour and light are in the state it needs

- [Postfix RenderTarget.ResolveAttachments for a post-resolve pass](howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-1.md) — advanced
- [Postfix SuperMeshRenderSystem.RenderMainPass to draw inside the opaque scene pass](howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-2.md) — advanced
- [Postfix SuperMeshRenderSystem.RenderTranslucencyPass with tracked barriers](howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-3.md) — advanced
- [Prefix SunbloomRenderer.Render to dispatch compute on the HDR scene](howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-4.md) — advanced
- [Transpile Program.RenderGame around the swapchain render pass](howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-5.md) — advanced

### [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md)

From the pre-bloom hook a mod can read the scene colour and depth and write back into it with compute only, and KSA compiles the mod's .comp files when they are declared as Shader assets

- [Depth-reading compute dispatches for rings, holes, tracers and relighting](howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-1.md) — advanced
- [In-place rescale using KSA's light pre-pass images](howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-2.md) — advanced
- [Add a fireball's diffuse term where the pre-pass culled it beyond about 3 km](howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-3.md) — advanced

### [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/overview.md)

One-shot uploads, offscreen renders and ImGui-to-texture need command buffers the game does not own, so pick the pool, fence and queue lock that neither stall nor auto-submit half-recorded work

- [Private one-shot command buffer and fence, with StagingPool used only for memory](howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/submit-your-own-vulkan-commands-1.md) — advanced
- [Mod command pool, fence ring and locked queue submit](howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/submit-your-own-vulkan-commands-2.md) — advanced
- [Private transient pool for readback and thumbnails](howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/submit-your-own-vulkan-commands-3.md) — advanced
- [Private images, runtime GLSL and a fenced submit from StarMapBeforeGui](howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/submit-your-own-vulkan-commands-4.md) — advanced

## Overlays

Projecting world and sky points to the screen and drawing overlays that stay glued to bodies, craft and the sky.

### [Choose a render frame and anchor effects to bodies, parts and terrain](howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)

Ecl positions keep the 29.8 km/s orbital carrier while ego positions are camera-relative translations, so picking the right frame and sample instant keeps overlays, effects and decals on the craft, planet or part they belong to

- [Two-instant draw anchor against GetPositionEgo](howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-1.md) — intermediate
- [Anchor to the engine's own render position and add travel since launch](howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-2.md) — advanced
- [Explosion presets with a BubbleOrigin fixed in the body frame](howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-3.md) — intermediate
- [Pooled emitters re-anchored to the celestial's rotating frame every frame](howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-4.md) — advanced
- [Body-fixed mesh with Ccf-to-Cce rotation and body position at draw time](howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-5.md) — advanced
- [Part-local, cloth-barycentric or geodetic anchors with double-precision composition](howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-6.md) — advanced

### [Draw lines and labels on the sky](howto/rendering/overlays/draw-lines-on-the-sky/overview.md)

Draw constellation lines, boundary polylines, star names and labelled RA/Dec grids on KSA's sky from catalog data in a click-through ImGui overlay that tracks the camera, with J2000 conversion to the ecliptic frame the game draws in

- [ImGui window draw list from StarMapAfterGui with ego-space sky directions](howto/rendering/overlays/draw-lines-on-the-sky/draw-lines-on-the-sky-1.md) — intermediate
- [One-degree arcs, clip-space frustum cull and CalcTextSize label rectangles](howto/rendering/overlays/draw-lines-on-the-sky/draw-lines-on-the-sky-2.md) — intermediate

### [Draw markers, brackets and measurement annotations on world objects](howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/overview.md)

Put a bracket on a craft, a lock cue on a contact, a measurement line with a plated label on a hull, a transfer-window gizmo in the map view or a pointer to a selected body using projected points on an ImGui draw list rather than Vulkan

- [Constant-pixel-size brackets, cues and bars projected from craft and contact positions](howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-1.md) — intermediate
- [Lines, curves, rings and plated labels for measurements](howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-2.md) — intermediate
- [Map-mode markers and gizmos for future positions](howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-3.md) — advanced
- [Previous-frame window rectangle plus edge-clamped projection (body target)](howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-4.md) — intermediate
- [Same technique for a catalog star on the sky sphere](howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-5.md) — intermediate

### [Draw world-space lines and gizmos with the game's own renderers](howto/rendering/overlays/draw-world-space-lines-and-gizmos/overview.md)

Use the game's gizmo renderer for debug-style depth-tested world overlays that need no Vulkan code, and preview extra predicted orbits through the game's own line instances

- [Submit spheres and lines from the StarMap GUI postfix between reset and render](howto/rendering/overlays/draw-world-space-lines-and-gizmos/draw-world-space-lines-and-gizmos-1.md) — intermediate
- [Gizmos next to a moving craft using a frame-epoch anchor](howto/rendering/overlays/draw-world-space-lines-and-gizmos/draw-world-space-lines-and-gizmos-2.md) — intermediate
- [FlightPlan.AddLineInstances per plan with dimmed colours, postfix hook and quantized cache keys](howto/rendering/overlays/draw-world-space-lines-and-gizmos/draw-world-space-lines-and-gizmos-3.md) — advanced

### [Project world and sky positions to screen pixels and point at off-screen targets](howto/rendering/overlays/project-world-positions-to-screen/overview.md)

Turn a position in the game's frames into an ImGui screen coordinate, including behind-camera points, edge cues for off-screen targets, the viewport offset, sky directions and CPU occlusion by bodies

- [Body-fixed or orbit-relative anchors lifted to ecliptic, then Camera.EclToScreen](howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-1.md) — intermediate
- [Ego-space direction times a sphere radius, then Camera.EgoToScreen plus viewport origin](howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-2.md) — intermediate
- [Behind-camera NaN culling with polyline breaking](howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-3.md) — beginner
- [Clip-space edge clamp for off-screen and behind-camera targets](howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-4.md) — intermediate
- [Edge cue from the camera basis (bearing), not the projection](howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-5.md) — intermediate
- [Per-frame ego-space sphere list and ray test per overlay point](howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-6.md) — intermediate

## Textures and decals

Getting pixels onto the screen and onto surfaces: texture upload, ImGui images, replacing game textures, UI rendered to textures, world quads, projected decals and part recolouring.

### [Draw a textured quad attached to a part or the camera](howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/overview.md)

Put an image, sticker, screen or pixel sprite into the 3D scene as a world-space quad with depth testing, tied to a part transform or a view-space billboard

- [Opaque pixel quads with stock UnlitMesh shaders inside the offscreen scene pass](howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-1.md) — intermediate
- [A single textured quad via the same hook with its own pipeline setup](howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-2.md) — intermediate
- [Premultiplied-alpha translucent quad after the translucency pass](howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-3.md) — advanced

### [Recolour vessel parts and kittens with a patched part shader](howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md)

Tint, multiply or replace the colour of part instances, part types or whole vessels by packing colour into per-instance state bits and compiling a modified fragment shader, plus coloured emissive glow per subpart and engine heat glow overrides

- [7:7:7 colour in the unused StateBitFlag high bits plus a patched MeshIndirect.frag](howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-1.md) — advanced
- [Shader transform at compile time with packed colours and runtime masters](howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-2.md) — advanced
- [ADD_EMISSIVE_COLOR state bit and instance colour in a postfix on WriteState](howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-3.md) — advanced
- [Prefix on PartModelDynamic.AddInstance rewrites Temperature and TfiThickness](howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-4.md) — advanced

### [Project decals onto vessels, terrain and clutter](howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/overview.md)

Draw an image, a sticker or a damage mark onto arbitrary surfaces by reconstructing the scene position from the depth buffer in the post-resolve window or a compute pass, or use the terrain's own decal modifiers

- [Unit-cube decal volumes with reverse-Z depth reconstruction](howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-1.md) — advanced
- [Sticker pass after resolve with runtime GLSL including the game library](howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-2.md) — advanced
- [Procedural bullet holes painted by compute from depth, stored in the struck subpart's frame](howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-3.md) — advanced
- [Engine terrain DecalModifier deserialized from XML in code](howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-4.md) — advanced

### [Render ImGui UI into a Vulkan texture for use in the 3D world](howto/rendering/textures-and-decals/render-imgui-into-a-texture/overview.md)

Run a second ImGui context and a second Vulkan ImGui backend on a mod-owned render pass so a whole UI becomes an image shown in the scene or in another window, or put a live camera feed on a part

- [Secondary ImGui context sharing the game's atlas, second backend, fence-ring submission](howto/rendering/textures-and-decals/render-imgui-into-a-texture/render-imgui-into-a-texture-1.md) — advanced
- [BindlessTextures.SetTexture on the monitor's material to a secondary viewport's colour image](howto/rendering/textures-and-decals/render-imgui-into-a-texture/render-imgui-into-a-texture-2.md) — advanced
- [ImGui rendered to a texture and drawn as a world-space quad](howto/rendering/textures-and-decals/render-imgui-into-a-texture/render-imgui-into-a-texture-3.md) — advanced

### [Replace a game texture or model texture with a user PNG at runtime](howto/rendering/textures-and-decals/replace-a-game-texture-at-runtime/overview.md)

Swap what an existing material samples by re-pointing the bindless slot the game already uses or by rebuilding a mod-owned mesh resource with a PNG override and live UV remap, and give mod-built render objects a private material buffer

- [BindlessTextureLibrary.SetTexture on the stock slot with deferred destruction](howto/rendering/textures-and-decals/replace-a-game-texture-at-runtime/replace-a-game-texture-at-runtime-1.md) — intermediate
- [PNG override rebuilds the mod mesh's private resource; UV scale and offset re-uploads the vertex buffer](howto/rendering/textures-and-decals/replace-a-game-texture-at-runtime/replace-a-game-texture-at-runtime-2.md) — advanced
- [Thread-static construction context scopes transpiler and prefix redirection](howto/rendering/textures-and-decals/replace-a-game-texture-at-runtime/replace-a-game-texture-at-runtime-3.md) — advanced

### [Show a PNG or runtime-generated image in an ImGui window](howto/rendering/textures-and-decals/show-an-image-in-imgui/overview.md)

Decode or build pixels, upload them as a Vulkan texture, register with the ImGui Vulkan backend and draw with ImGui.Image or a draw list, then update, evict and release the texture without crashing a frame in flight

- [One static PNG decoded to TextureAsset, uploaded once](howto/rendering/textures-and-decals/show-an-image-in-imgui/show-an-image-in-imgui-1.md) — beginner
- [Many changing images with in-place refresh and LRU eviction](howto/rendering/textures-and-decals/show-an-image-in-imgui/show-an-image-in-imgui-2.md) — advanced

### [Upload an image or procedural pixels as a GPU texture](howto/rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/overview.md)

Load PNG or KTX bytes or generate pixels in code into a SimpleVkTexture with mip levels and a sampler and expose it to shaders through a bindless slot

- [Decode with the game's TextureLoader and re-point an existing bindless slot](howto/rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/upload-an-image-as-a-vulkan-texture-1.md) — intermediate
- [Mip-mapped library textures claimed into Program.BindlessTextures, hot-swapped on rescan](howto/rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/upload-an-image-as-a-vulkan-texture-2.md) — intermediate
- [Procedural ASCII pixel art to an R8G8B8A8 texture with a staging upload](howto/rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/upload-an-image-as-a-vulkan-texture-3.md) — beginner

## Meshes

Drawing and hiding geometry through the engine's own renderers: mod meshes, GPU buffer headroom, shadow casters, hidden parts and distance culling.

### [Make static objects cast sun and lamp shadows with invisible casters](howto/rendering/meshes/add-invisible-shadow-casters/overview.md)

Static objects never enter a shadow pass, so add depth-only caster meshes to the SuperMesh shadow pool and let a building, roof or pillar block sunlight and lamp light without drawing a second mesh

- [Add invisible depth-only shadow casters to the SuperMesh shadow pool](howto/rendering/meshes/add-invisible-shadow-casters/add-invisible-shadow-casters.md) — advanced

### [Draw your own meshes through the engine's mesh renderers](howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md)

Hand the game your meshes as StaticMeshRenderables, extra part-model instances or private buffers inside the static-object pipeline so lighting, shadows and culling come for free

- [Submit StaticMeshRenderables from a ClearBuckets postfix for every viewport](howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-1.md) — intermediate
- [Draw private vertex, index and material buffers inside the static-object pipeline](howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-2.md) — advanced
- [Add extra part-model instances at any matrix, per viewport](howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-3.md) — intermediate
- [Clone interleaved part meshes into private SimpleVkMeshes for another renderer](howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-4.md) — advanced

### [Reserve or grow game GPU buffers for content added at runtime](howto/rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md)

The game sizes its shared mesh buffer and star instance buffer at load, so runtime meshes or more stars need headroom reserved before the buffer exists or the buffer re-created while frames are in flight

- [Inflate DeviceMeshInterleaved.Shared before the first bind allocates the buffer](howto/rendering/meshes/grow-game-gpu-buffers-for-runtime-content/grow-game-gpu-buffers-for-runtime-content-1.md) — advanced
- [Re-create the star instance buffer by reflection and restyle stars with a Harmony prefix](howto/rendering/meshes/grow-game-gpu-buffers-for-runtime-content/grow-game-gpu-buffers-for-runtime-content-2.md) — advanced

### [Hide part meshes or subparts without removing them](howto/rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md)

Remove a window pane, a model or a missile from the picture while the part stays in the vessel, by filtering instance lists, scaling a subpart to nothing or moving it, and also show IVA interiors from outside and draw kitten equipment in the right order

- [Hide part models by compacting the instance lists around PartTreeRenderData.Compose](howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-1.md) — advanced
- [Scale a pre-declared subpart to nothing until it is needed](howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-2.md) — intermediate
- [Pose or hide subparts from StarMapAfterGui with an AfterOnFrame fallback](howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-3.md) — intermediate
- [Show IVA interiors from outside with an owner-counted Template.Internal requirement](howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-4.md) — advanced
- [Flip Template.Internal from a lazily installed dedicated Harmony instance](howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-5.md) — intermediate
- [Render kitten equipment parts before the instance upload with a nonvirtual Vehicle.UpdateRenderData call](howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-6.md) — advanced

### [Keep a vessel or kitten rendered at any distance](howto/rendering/meshes/keep-vessels-rendered-at-any-distance/overview.md)

KSA culls vehicles that project below one pixel; override the cull so a distant ship, a projectile launcher or a scaled-up vessel still draws

- [Skip the one-pixel cull with prefixes on Vehicle.GetWorldMatrix and UpdateRenderData](howto/rendering/meshes/keep-vessels-rendered-at-any-distance/keep-vessels-rendered-at-any-distance-1.md) — intermediate
- [Replace the sub-pixel cull with prefixes installed lazily on a dedicated Harmony instance](howto/rendering/meshes/keep-vessels-rendered-at-any-distance/keep-vessels-rendered-at-any-distance-2.md) — intermediate
- [Postfix Vehicle.UpdateRenderData and ask the pixel test again for a launcher with rounds in flight](howto/rendering/meshes/keep-vessels-rendered-at-any-distance/keep-vessels-rendered-at-any-distance-3.md) — intermediate
- [Enlarge the cull radius with call-site transpilers so a scaled vessel is culled sanely](howto/rendering/meshes/keep-vessels-rendered-at-any-distance/keep-vessels-rendered-at-any-distance-4.md) — advanced

## Lights and particles

Lights, particles, explosions and exhaust plumes submitted through the engine's own systems.

### [Add dynamic spot and point lights to the world from a mod](howto/rendering/lights-and-particles/add-dynamic-lights-to-the-world/overview.md)

KSA clears its light list every frame, so a mod submits its own Light instances after ClearLights and before the lights are written, for headlights, lamps, flashes and fireballs, and budgets which ones cast shadows

- [Postfix PartTree.UpdateRenderData per craft and submit spot lights with CreateSpotLight](howto/rendering/lights-and-particles/add-dynamic-lights-to-the-world/add-dynamic-lights-to-the-world-1.md) — intermediate
- [Submit per-viewport lights from an UpdateStaticObjectRenderData postfix with a shadow-caster budget](howto/rendering/lights-and-particles/add-dynamic-lights-to-the-world/add-dynamic-lights-to-the-world-2.md) — advanced
- [Push a point light through the public LightDebug list, with a compute fill beyond 3 km](howto/rendering/lights-and-particles/add-dynamic-lights-to-the-world/add-dynamic-lights-to-the-world-3.md) — advanced

### [Control part lights from code: state, colour, intensity and beam angles](howto/rendering/lights-and-particles/control-part-light-color-and-state/overview.md)

Switch the lights on stock light parts and vehicle lamps, recolour them, set intensity and narrow or widen spotlight cones, for one instance without affecting the shared template, and build grids and animations out of such lights

- [Write colour, intensity and switch state into the shared PartTemplate light component](howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-1.md) — intermediate
- [Clone the shared light template per instance and set cone angles](howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-2.md) — intermediate
- [Build grids of light parts at runtime and switch each pixel through its stock light switch](howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-3.md) — advanced
- [Animate a light with a bounded queue of eased single-step transitions](howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-4.md) — intermediate
- [Run disco channels on private light templates with owned actuators](howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-5.md) — advanced

### [Draw volumetric exhaust plumes and smoke trails without a real engine](howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/overview.md)

Reuse the game's volumetric exhaust renderer for flames from a mod-owned thrust source by building VolumetricExhaustInstances and plume data yourself, edit the shared templates live and lay volumetric smoke trails behind projectiles and flares

- [Template-based plume instances with RocketNozzle.ComputePlumeData and mod-made gas numbers](howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-1.md) — advanced
- [Postfix AddVolumetricExhaustInstances with an isentropic nozzle model](howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-2.md) — advanced
- [Edit the shared exhaust templates live and propagate to instances and nozzles](howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-3.md) — intermediate
- [Reflect into private editor fields and replay each editor's apply sequence](howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-4.md) — advanced
- [Lay volumetric smoke trails with one PlumeTrailEmitterState cursor per source](howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-5.md) — advanced

### [Spawn particle effects and explosions from a mod](howto/rendering/lights-and-particles/spawn-particle-effects/overview.md)

Take emitters from the engine's fixed pool, either from XML-declared ParticleEmitter assets or built in C#, anchor them, return them correctly, and fire the game's own explosion presets where no vehicle hosts the burst

- [Take XML-declared ParticleEmitter assets by id from the engine pool and give them back correctly](howto/rendering/lights-and-particles/spawn-particle-effects/spawn-particle-effects-1.md) — intermediate
- [Build a ParticleEmitterReference in C# with SpawnMode.Burst](howto/rendering/lights-and-particles/spawn-particle-effects/spawn-particle-effects-2.md) — intermediate
- [Fire the game's own ExplosionSystem presets from a point anchored on the planet](howto/rendering/lights-and-particles/spawn-particle-effects/spawn-particle-effects-3.md) — intermediate
