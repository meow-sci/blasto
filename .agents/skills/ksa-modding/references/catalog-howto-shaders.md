# Shaders

The shader-authoring side of KSA modding - writing GLSL post-processing passes, declaring ShaderExtensions assets, feeding them parameters from C#, building common screen effects, compute effects in the HDR scene, and replacing or patching the game's own GLSL without editing game files, plus the tooling to include, compile, validate and hot-reload shaders.

## Post processing

ShaderExtensions post-processing passes: the fragment-shader contract, XML assets, pre- versus post-ImGui placement, uniform buffers and push constants.

### [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](howto/shaders/post-processing/choose-pre-or-post-imgui-post-processing/overview.md)

What a PostProcessingShader and a GlobalPostShader each see, where they run relative to the tonemap composite, CMAA2 and the UI, and how to pick the stage for an effect

- [Choose the stage: a pre-ImGui chain on the RGBA16F scene, copied back before the tonemap](howto/shaders/post-processing/choose-pre-or-post-imgui-post-processing/choose-pre-or-post-imgui-post-processing-1.md) — intermediate
- [Choose the stage: a post-ImGui chain on the presented image via swapchain copies](howto/shaders/post-processing/choose-pre-or-post-imgui-post-processing/choose-pre-or-post-imgui-post-processing-2.md) — intermediate

### [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/overview.md)

The four ShaderExtensions XML asset types, how Id, RenderPassId, paths and binding children fit together, and how references let a later pass read an earlier pass's image

- [Declare post-processing assets: paired pre and post passes sharing a RenderPassId and buffer](howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-1.md) — beginner
- [Declare post-processing assets: a small file with a chain, an input reference and an ImGuiShader](howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-2.md) — intermediate
- [Declare post-processing assets: input and output references resolved while the chain is built](howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-3.md) — advanced

### [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md)

How a mod defines a C# struct for a GLSL std140 uniform block, registers it as an XML element without referencing ShaderExtensions, and writes it each frame

- [Feed a shader with uniform buffers: Pack=1 structs with manual std140 padding, one block per effect](howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-1.md) — intermediate
- [Feed a shader with uniform buffers: share one buffer across passes (Size owns it, Id references it)](howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-2.md) — intermediate
- [Feed a shader with uniform buffers: Span, pointer and BufferEx lookups beside push constants in a HUD mod](howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-3.md) — intermediate

### [Send per-frame values and time to a shader with push constants, written at the right point in the frame](howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md)

How a ShaderExtensions push constant becomes a Vulkan push-constant range, which clock a shader can read, how to switch an always-on pass off, and when in the frame to write

- [Push constants for a shader: one element per pass with an enabled flag and time, written from OnFrameEditor](howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-1.md) — intermediate
- [Push constants for a shader: per-side clock and strengths in a settings-driven mod](howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-2.md) — intermediate
- [Time in a shader: the ShaderTime block at binding 1 of every ShaderEx set](howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-3.md) — beginner
- [Time in a stock shader: signed simulation seconds smuggled through a stock atmosphere field](howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-4.md) — advanced

### [Write a fullscreen post-processing fragment shader for KSA](howto/shaders/post-processing/write-a-post-processing-fragment-shader/overview.md)

The contract a ShaderExtensions pass shader is written against, the extra bindings it can declare, and how the mod builds each pass from a RenderTechnique and a RenderTarget

- [Write a post-processing fragment shader: the minimal identity-for-neutral pass (MPFX contract)](howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-1.md) — beginner
- [Write a post-processing fragment shader: extra textures and the port from subpasses to dynamic rendering](howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-2.md) — intermediate
- [Write a post-processing fragment shader: how a pass is built from RenderTechnique and RenderTarget](howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-3.md) — advanced
- [Write a post-processing fragment shader: a ShaderReference subclass that builds its own descriptor set](howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-4.md) — advanced

## Stock shaders

Changing the game's own shaders: redirected and text-patched copies, in-memory patching at compile time, smuggled data, per-body branches and sky and cloud extensions.

### [Branch per body inside shared game shaders](howto/shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/overview.md)

One stock shader draws every body, so per-body behaviour has to be keyed on values the shader can already see, such as the body radius plus an XML-written number, or a point's own height

- [Branch per body in shared shaders: measured ln-density tables recognised by radius and an exact scale height](howto/shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/branch-per-body-inside-shared-game-shaders-1.md) — advanced
- [Branch per body in shared shaders: a relief-aware terminator closing over a sqrt(2 relief / R) band](howto/shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/branch-per-body-inside-shared-game-shaders-2.md) — advanced

### [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md)

How the sky and volumetric cloud passes are structured, and four text-patch recipes that add a per-pixel march over the sky LUTs and fix cloud lighting, handover and ring shadows

- [Extend the sky shader: a per-pixel raymarch inserted into Atmosphere.comp and blended by tangent altitude](howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/extend-stock-sky-and-cloud-shaders-1.md) — advanced
- [Fix cloud lighting: light a thick raymarch step at its first optical depth by bisection](howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/extend-stock-sky-and-cloud-shaders-2.md) — advanced
- [Fix the cloud handover: flow and lift the volumetric map to match the 2D layer](howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/extend-stock-sky-and-cloud-shaders-3.md) — advanced
- [Reuse stock helpers in a cloud shader: include RingShadows.glsl and select mips without derivatives](howto/shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/extend-stock-sky-and-cloud-shaders-4.md) — advanced

### [Patch vehicle paint and canopy shaders in memory at compile time](howto/shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/overview.md)

How a Harmony prefix on ShaderModuleUtils.FromFile and Utils.SetShaderFromMod can compile transformed GLSL source instead of the file on disk, and how to make the game recompile

- [Patch shaders in memory: a MeshIndirect.frag patch for vehicle paint, recompiled on a requested rebuild](howto/shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/patch-game-shaders-in-memory-at-compile-time-1.md) — advanced
- [Patch shaders in memory: Model.vert, Model_Skinned.vert and ModelPbr.frag patched for canopy projection](howto/shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/patch-game-shaders-in-memory-at-compile-time-2.md) — advanced
- [Patch shaders in memory: a compile-time transform with packed colours, a conflict check and runtime masters](howto/shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/patch-game-shaders-in-memory-at-compile-time-3.md) — advanced

### [Replace or text-patch the game's GLSL without editing game files](howto/shaders/stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/overview.md)

How KSA compiles its own shaders from the Content/Core/Shaders tree and the one seam, ShaderModuleUtils.FromFile, that lets a mod compile patched copies instead

- [Replace or text-patch the game's GLSL: a shadow tree, a FromFile redirect and anchor-verified edits](howto/shaders/stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/replace-or-patch-game-glsl-without-editing-game-files.md) — advanced

### [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/overview.md)

Stock shaders have fixed inputs, so a patched shader borrows fields that are free or irrelevant for the case at hand and decodes them in GLSL

- [Smuggle data into stock shaders: a temporary value swap in a prefix, or an edit of the boxed struct in a postfix](howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-1.md) — advanced
- [Smuggle data into stock shaders: spare high bits of the part state flags](howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-2.md) — advanced
- [Smuggle data into stock shaders: a marker in a material's ExtraData plus a shader-side branch](howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-3.md) — advanced
- [Smuggle data into stock shaders: cut the stock MiePhaseFunction and insert a dispatcher on asymmetry sentinels](howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-4.md) — advanced

## Glsl workflow

Writing and maintaining mod GLSL: including the game's shader library, compiling at runtime, hot reload in a running game and offline compile checks.

### [Compile-check a mod's GLSL without launching the game](howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)

Why KSA only compiles mod shaders at load, how glslangValidator can stand in for the game's shaderc with an include search path, and how to keep shader constants in step with C# by text tests

- [Check shaders offline: check-shaders.sh in a pre-push hook and CI, skipped without an install](howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/check-mod-shaders-offline-with-glslang-1.md) — beginner
- [Check shaders offline: a per-folder check beside asset and mesh validation](howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/check-mod-shaders-offline-with-glslang-2.md) — beginner
- [Check shaders offline: keep GLSL constants in step with C# using a regex text test](howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/check-mod-shaders-offline-with-glslang-3.md) — intermediate

### [Compile and write GLSL at runtime for a mod's own pipeline](howto/shaders/glsl-workflow/compile-glsl-at-runtime/overview.md)

How a mod compiles an embedded GLSL string with the game's shaderc wrapper, what debug path and includes it needs, and three mods that build their own quad, decal or preview pipeline this way

- [Compile GLSL at runtime: a premultiplied-alpha fragment shader paired with the stock unlit vertex shader](howto/shaders/glsl-workflow/compile-glsl-at-runtime/compile-glsl-at-runtime-1.md) — advanced
- [Compile GLSL at runtime: a sticker fragment shader compiled with FromString and the game's includes](howto/shaders/glsl-workflow/compile-glsl-at-runtime/compile-glsl-at-runtime-2.md) — advanced
- [Compile GLSL at runtime: derivative normal mapping in an embedded-resource preview shader](howto/shaders/glsl-workflow/compile-glsl-at-runtime/compile-glsl-at-runtime-3.md) — advanced

### [Include KSA's own GLSL library from a mod shader](howto/shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md)

How KSA resolves shader includes and why a mod needs a generated header with absolute paths to reuse Global.glsl, AtmosphereLuts.glsl and TextureSet.glsl

- [Include the game's shader library: a generated Shaders/Content/CoreAtmosphere.glsl pulling in atmosphere and texture headers](howto/shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/include-the-game-shader-library-in-mod-glsl-1.md) — intermediate
- [Include the game's shader library: a generated KSAStructuresCore.glsl under a folder called Content](howto/shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/include-the-game-shader-library-in-mod-glsl-2.md) — intermediate
- [Include the game's shader library: a relative include of a stock helper inside a patched copy](howto/shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/include-the-game-shader-library-in-mod-glsl-3.md) — intermediate

### [Recompile a mod's shader while the game runs](howto/shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/overview.md)

Why KSA's ShaderReloader cannot see a mod's shader and how to recompile one by calling FileReference.DoLoad through reflection and rebuilding the pipelines that held the old module

- [Reload a mod shader: a bridge command that invokes FileReference.DoLoad by reflection and drops cached pipelines](howto/shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/reload-a-mod-shader-in-a-running-game-1.md) — intermediate
- [Reload a mod shader: a reload command in a developer file bridge that an agent can drive](howto/shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/reload-a-mod-shader-in-a-running-game-2.md) — intermediate

## Screen effects

Ready-made full-screen effects: colour grading, blur, grain and dithering, CRT curvature on one ImGui window, refraction and view shake.

### [Add film grain and dither away banding](howto/shaders/screen-effects/add-film-grain-and-blue-noise-dithering/overview.md)

Two ways to add noise to the final image in KSA, procedural simplex grain blended soft-light with a push-constant clock, and a spatiotemporal blue-noise texture indexed by pixel and time slice

- [Film grain: Ashima/Gustavson noise with soft-light blending on a push-constant clock](howto/shaders/screen-effects/add-film-grain-and-blue-noise-dithering/add-film-grain-and-blue-noise-dithering-1.md) — intermediate
- [Dither with blue noise: a 128x128x64 STBN texture stacked in a 128x8192 PNG, slice picked from time](howto/shaders/screen-effects/add-film-grain-and-blue-noise-dithering/add-film-grain-and-blue-noise-dithering-2.md) — intermediate

### [Bend the picture with CRT curvature and chromatic aberration](howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/overview.md)

Remap UVs for barrel distortion and offset colour channels by a luminance-masked amount, applied to one tagged ImGui window or to a whole canopy-glass composite, plus how the host replays a tagged window offscreen

- [Bend the picture: barrel curve within UvRect and channel offsets in an ImGuiShader](howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/bend-the-picture-with-crt-curvature-and-chromatic-aberration-1.md) — intermediate
- [Bend the picture: chromatic aberration and radial edge blur in a glass composite pass](howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/bend-the-picture-with-crt-curvature-and-chromatic-aberration-2.md) — intermediate
- [Bend the picture: how the host replays a tagged ImGui window offscreen and rewrites it to one image](howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md) — advanced

### [Blur the screen with a two-pass separable Gaussian](howto/shaders/screen-effects/blur-the-screen-with-a-separable-gaussian/overview.md)

How to blur the whole frame with a horizontal and a vertical ShaderExtensions pass, whether the weights come from the CPU or from a fixed non-linear tap distribution, and how to keep a sharp copy for a composite

- [Blur the screen: CPU-computed normalized weights up to radius 20 in a std140 block](howto/shaders/screen-effects/blur-the-screen-with-a-separable-gaussian/blur-the-screen-with-a-separable-gaussian-1.md) — intermediate
- [Blur the screen: a non-linear tap distribution and a refracted horizontal pass](howto/shaders/screen-effects/blur-the-screen-with-a-separable-gaussian/blur-the-screen-with-a-separable-gaussian-2.md) — intermediate

### [Colour-grade the frame and add an aspect-correct vignette](howto/shaders/screen-effects/colour-grade-the-frame-and-add-a-vignette/overview.md)

Where in KSA's frame a colour grade belongs, the GLSL building blocks MPFX uses for lift/gamma/gain, temperature, hue, contrast, saturation and vignette, and a TV or thermal look for a secondary camera feed

- [Colour-grade the frame: pre-ImGui grading passes on the HDR scene](howto/shaders/screen-effects/colour-grade-the-frame-and-add-a-vignette/colour-grade-the-frame-and-add-a-vignette-1.md) — intermediate
- [Colour-grade a camera feed: a two-dispatch measure and map pass per viewport with a cached pipeline](howto/shaders/screen-effects/colour-grade-the-frame-and-add-a-vignette/colour-grade-the-frame-and-add-a-vignette-2.md) — advanced

### [Refract and distort the rendered scene, and flash or shake it](howto/shaders/screen-effects/refract-and-distort-the-scene/overview.md)

Fake glass, lenses and shock rings by sampling the scene at an offset, in a fragment pass with a normal mask or in compute passes that copy the HDR scene and write it back displaced, plus whiteouts and view shake without touching the camera

- [Refract the scene: normal-mask UV offsets and luminance-gated dirt in a fragment pass](howto/shaders/screen-effects/refract-and-distort-the-scene/refract-and-distort-the-scene-1.md) — intermediate
- [Refract the scene: a scene copy then a displaced write-back in two compute dispatches](howto/shaders/screen-effects/refract-and-distort-the-scene/refract-and-distort-the-scene-2.md) — advanced
- [Flash and shake the view: a flash-only mode of a compute shader and a shock-pass shake on the sim clock](howto/shaders/screen-effects/refract-and-distort-the-scene/refract-and-distort-the-scene-3.md) — advanced

## Compute effects

Compute-shader effects in the HDR scene: packing parameters, reconstructing positions from depth and raymarching local volumes.

### [Pack parameters and camera data for a compute effect](howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/overview.md)

How to fit a compute effect's state into 128 bytes of push constants or a host-visible storage buffer, run several effects through one pipeline, and give the pass a camera pose that survives float precision and map mode

- [Pack a compute effect into 128 bytes: octahedral vectors and an exact flag sum](howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-1.md) — advanced
- [Run several effects through one compute pipeline: a kind tag and a per-kind dispatch cap](howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-2.md) — advanced
- [Feed a compute shader thousands of items through a host-visible storage buffer](howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-3.md) — advanced
- [Give a compute pass the camera pose without losing precision, or the wrong camera](howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-4.md) — advanced

### [Raymarch a local volume or analytic sky effect into the HDR scene](howto/shaders/compute-effects/raymarch-a-local-volume-into-the-hdr-scene/overview.md)

How to draw smoke, fire, clouds and sky glows with a compute raymarch recorded before bloom, lit and occluded by KSA's own atmosphere and weather data, and priced from the game's GPU profiler

- [Raymarch a noise-eroded volume into the HDR scene: bounding sphere, jitter and a reprojected resolve](howto/shaders/compute-effects/raymarch-a-local-volume-into-the-hdr-scene/raymarch-a-local-volume-into-the-hdr-scene-1.md) — expert
- [Light a mod volume with KSA's sky and hide it behind weather clouds](howto/shaders/compute-effects/raymarch-a-local-volume-into-the-hdr-scene/raymarch-a-local-volume-into-the-hdr-scene-2.md) — expert
- [Draw analytic sky effects in a compute pass: X-ray glow, curtains and debris shells chosen by the air](howto/shaders/compute-effects/raymarch-a-local-volume-into-the-hdr-scene/raymarch-a-local-volume-into-the-hdr-scene-3.md) — expert

### [Reconstruct scene positions from the depth buffer and paint on them](howto/shaders/compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md)

How a compute pass rebuilds each pixel's camera-relative world point from reverse-Z depth to draw exact rings, hull holes and depth-tested streaks in the HDR scene without any geometry

- [Paint an exact ring on the ground: depth reconstruction, one dispatch per ring](howto/shaders/compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/reconstruct-scene-positions-from-depth-in-a-compute-shader-1.md) — advanced
- [Paint holes that stay on a moving hull: sub-part frames and a tile bitmask gather](howto/shaders/compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/reconstruct-scene-positions-from-depth-in-a-compute-shader-2.md) — advanced
- [Draw thousands of depth-tested streaks: project on the CPU, gather per tile, add before bloom](howto/shaders/compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/reconstruct-scene-positions-from-depth-in-a-compute-shader-3.md) — advanced
