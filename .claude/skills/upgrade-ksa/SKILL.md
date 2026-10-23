---
name: upgrade-ksa
description: Validate Blasto against an upstream Kitten Space Agency (KSA) game update. Use when a new KSA build lands and you must decide whether Blasto needs code changes to run correctly on it. Enumerates every coupling point between Blasto and KSA/Brutal/Planet/StarMap game code (Harmony patch targets and the PlanetRenderer transpiler, private-field reflection, the private terrain pipeline/descriptor set 2, Planet shader source anchors, worker-thread nozzle capture, surface sampling, native dust particles, save/load hooks, UI hooks, lifecycle and build refs) and drives a diff-of-KSA → impact-on-Blasto review. Requires the CURRENT (new) and PREVIOUS KSA decompiled sources to be supplied — never hard-codes machine paths.
---

# Upgrade / Re-validate Blasto against a KSA game change

Blasto is a StarMap mod that leaves persistent rocket-exhaust marks and dust on terrain. It
compiles against KSA's reference assemblies and then reaches deep into the game:

- It **replaces KSA's terrain pipelines** with private copies. These use transformed `Planet.*`
  shaders and a cloned descriptor set 2 with an extra storage buffer at binding 13.
- It **rewrites `PlanetRenderer.Render` IL** with a transpiler that swaps that set at the native
  descriptor-bind call.
- It **reads private `PlanetRenderer` fields**.
- It **captures rocket-nozzle state on physics worker threads**.
- It **drives the native particle system** and **hooks the save/load transaction**.

When the game changes, any of this can break. Some breaks show up at compile time. Others make
the whole mod refuse to load. The most dangerous ones render, simulate or save **silently
wrong**. This skill is the checklist and method for deciding what Blasto must change for a new build.

Use the **`ksa`** skill for how KSA game types, APIs and the decomp work in general. Use the
**`harmony`** skill for patch mechanics. This skill covers only Blasto's *coupling surface*.
`INTEGRATION.md` is the repo's own boundary contract. Keep it consistent with what you find.

## Required inputs: you MUST have both KSA snapshots

This skill compares two decompiled KSA trees. **Do not proceed without them.** Ask the user for
the paths. Never hard-code paths; treat these as variables:

- **`<current-ksa>`**: the **new** KSA build you are upgrading *to*.
- **`<previous-ksa>`**: the build Blasto was **last validated on**. The record is the pin line in
  `README.md` / `INTEGRATION.md` / `VALIDATION.md` (there is no version check in code; see §1).
  Don't trust whatever a "previous" folder happens to hold. If the folder is at some other build,
  diff from the last validated build.

Each snapshot is a KSA-game-assemblies checkout:

```
<snapshot>/
  decomp/        # decompiled C#; folders are per NAMESPACE (decomp/KSA/PlanetRenderer.cs,
                 # decomp/KSA.Rendering.Planet/PlanetViewportData.cs, decomp/RenderCore/…,
                 # decomp/Brutal.VulkanApi/…), with one .csproj per assembly alongside
  dll/           # reference assemblies Blasto compiles against ($(KSAFolder) points here)
  Content/       # game assets, incl. Content/Core/Shaders/Planet/*.{vert,tesc,tese,frag}
  version.json   # { build, date, fromRevision, toRevision, commits[] }: human-readable changelog
```

The default sibling checkout `../ksa-game-assemblies` is a git repo with **one commit per
build**, with the commit subject set to the build string. Its `current/` folder holds whichever
build was copied last. You can therefore take both snapshots from history:
`git -C <repo> log --oneline` gives the SHAs, and
`git -C <repo> diff <validated-sha> <new-sha> -- current/decomp/KSA/PlanetRenderer.cs` diffs one file.

If the user gives you only one tree, or only DLLs, say what's missing. Also say what you can
still check (a compile against the new DLLs and the shader-anchor validation against the new
`Content/`) and what you can't (behavioral coupling; see below).

Read `version.json.commits[]` first. Lines mentioning terrain, planet rendering, shaders,
tessellation, particles, engines/nozzles, physics/threading or saves tell you where to focus.
**It can have gaps.** `fromRevision` is whatever the snapshot tool last saw, not necessarily
the validated build's `toRevision`. If they don't chain, the decomp diff is the only complete
record. Never conclude "nothing moved" from a short `commits[]`.

## The core insight: Blasto fails closed, so a green build proves little

Blasto deliberately **throws** on most drift instead of degrading. Every `Install` path runs
inside `Mod.BeforeMain`'s try/catch. If any patch target is missing or a private field is gone,
**the whole mod initializes nothing**: all patches are unpatched
and the window reports `Initialization failed: …`. Each problem of that kind fails loudly, and
there is no partial mode. The dangerous cases are the ones that still *pass* those checks:

| Coupling kind | Breaks at | Detected by |
|---|---|---|
| Direct call to a referenced member | **compile** | `dotnet build` against `<current-ksa>/dll` |
| **Build ref** (DLL renamed/split) | **compile**, as CS0246/CS0234 type errors | Every `<Reference>` is `Exists(...)`-conditioned, so a renamed DLL is dropped *silently* and surfaces only as missing types |
| **Harmony target / parameter name** | **load**: `MissingMethodException` or Harmony arg-binding error → whole mod disabled | Offline patch-apply harness (below), or the KSA log |
| **Private-field reflection** (`TerrainAccess`) | **load**: `TerrainAccess` static init throws → whole mod disabled | Offline harness |
| **Transpiler shape** (exactly one 6-arg `BindDescriptorSets<CommandBuffer>` in `PlanetRenderer.Render`) | **load**: transpiler throws → whole mod disabled | Offline harness (transpilers run at patch time) |
| **Runtime schema checks** (set-2 descriptor counts, stage count 2/4, GPU limits, shader anchors) | **first terrain build**: private pipeline refused, stock terrain kept, `BuildFailures++` | Diagnostics window / `[Blasto]` log; `scripts/validate-shaders.sh` for anchors |
| **Behavioral / semantic** (binding *numbers/stages* in set 2, pipeline-layout set order, coordinate frames, worker/main-thread ordering, `ThrustTime` meaning, particle opacity channel, save-call nesting) | **runtime, silent wrong output, or Vulkan validation errors / device loss** | Nothing automatic. Only diffing KSA source and reasoning about it catches these |
| **Asset / content ids** (`PlanetFrag`, `PlanetImpact`, body ids `Luna`/`Mars`, biome names) | **runtime**: throw (feature self-disables) or silent misclassification | Source/asset diff |

**The point of this skill** is the behavioral row. The loud failures will announce themselves.
A semantic drift in the terrain pipeline can render garbage. It can also trigger validation
errors inside **KSA's own terrain draw**, which affects every player, not just Blasto's feature.
Budget most of your effort there.

## Workflow

1. **Confirm the baseline.** Read the validated-build pin in the docs and `version.json` in
   both snapshots. Skim `commits[]` for subsystems that moved.
2. **Build Blasto against `<current-ksa>/dll`** to sweep up compile-visible breaks first (see
   [Build and offline checks](#build-and-offline-checks)). Fix or triage those, including §9 ref
   renames.
3. **Run the shader-anchor validation** against `<current-ksa>/Content/Core/Shaders` (§3).
4. **Walk the coupling catalog below.** For each touchpoint, open the named KSA type in
   **both** trees and diff the specific members Blasto depends on:
   ```bash
   diff <previous-ksa>/decomp/KSA/PlanetRenderer.cs <current-ksa>/decomp/KSA/PlanetRenderer.cs
   ```
   `PlanetRenderer` is the single highest-value diff. Read the changed regions in full, not just
   the hunks.
5. **For each change, map it to the Blasto file(s)** listed for that touchpoint and decide
   whether it needs no change, a mechanical fix or a behavioral redesign.
6. **Report** using the [output format](#output-format).
7. **Once the review clears (or the fixes land)**, record the
   [new validated build](#recording-a-validated-build). There is no runtime guard, so users are
   already running Blasto on the new build; the review decides whether that is safe.

Do **not** silently edit Blasto unless asked; the deliverable is a *review*. If asked to fix as
well, do the compile-visible fixes first, then propose the behavioral ones, and record the validated build last.

---

## Coupling catalog

Every place Blasto reaches into game code, grouped by kind. Paths are Blasto repo paths. The KSA
members are what to diff across the two snapshots. Navigate by symbol; line numbers drift.

### 1. Validated-build record (no runtime version guard)

- Blasto has **no game-version check**; it attempts to run on any KSA build (the hard guard in
  `TerrainBlastRenderer.Install` was removed after the 5638 review). Every other check in this
  catalog still applies, so a drifted build fails at the specific touchpoint instead. This means a
  new KSA build that ships before review **will** run Blasto's private terrain pipeline unreviewed.
  That's why the behavioral checks below matter more than they did with the guard in place.
- `Mod.cs` logs the running KSA version, and `BlastPersistence.Capture` stamps it into each
  sidecar's `GameBuild` (informational, not validated on read). These are the first things to
  check in a user's log when triaging.
- The **last validated build** lives only in docs: the pin lines in `README.md`,
  `INTEGRATION.md` and `VALIDATION.md`. Use those as `<previous-ksa>` unless the user says otherwise.

### 2. Harmony patches

All are installed in `BeforeMain` and **throw on a missing target**: `Mod.Patch` and
`BlastPersistence.Patch` use `AccessTools.Method(...) ?? throw`. Several patches bind original
parameters **by name**, so a parameter rename in KSA breaks patching even when types are
unchanged. Verify for each: **type exists · method exists · exact parameter types *and names* ·
the semantics Blasto depends on**.

| Patch (Blasto file) | KSA target | Binds by name | Depends on | If KSA changes… |
|---|---|---|---|---|
| `TerrainBlastRenderer.AfterBuild` postfix | private `PlanetRenderer.BuildRenderPipeline()` (`AccessTools.DeclaredMethod`) | `__instance` | Blasto **re-invokes** it via reflection after swapping `_renderShaderStages` modules and `_renderPipelineLayout`, then harvests the new `_solid`/`_wire` and restores the originals. This assumes the method (a) builds pipelines purely from those fields, (b) writes its results into `_solid`/`_wire`, and (c) does **not destroy** the pipelines currently held in `_solid`/`_wire` before overwriting them. If it did, the re-entrant call would destroy the stock pipelines | Rename → load failure. If the method starts destroying old handles, caching pipelines elsewhere, or building from other fields → stock terrain destroyed or private pipeline built with stock shaders/layout. **Read the whole method in both trees.** |
| `TerrainBlastRenderer.BeforeRender` prefix / `ReplaceForwardBinding` transpiler / `AfterRender` finalizer | `PlanetRenderer.Render(...)` | `inViewport`, `frameIndex`, `__instance`, `__state`, `__exception` | `Render` reads `_solid`/`_wire`/`_renderPipelineLayout` **during** the call (Blasto swaps them for the duration). The method body contains **exactly one** call to the generic `VkDeviceExtensions.BindDescriptorSets<CommandBuffer>` with 6 parameters `(cmd, bindPoint, layout, firstSet, ReadOnlySpan<VkDescriptorSet>, Span<ByteSize32>)`. That call binds all six forward sets starting at set 0, so span index 2 is set 2. `_planetViewportData[inViewport.ShaderSlot].NearbyCelestial` is the body being drawn. No nested forward render on the same thread | Param rename → load failure. A second bind call, or a different overload or arity → transpiler throws → load failure. **Silent:** the bind no longer starting at set 0, or binding a different set count (index 2 then points at the wrong set). Render caching handles before the prefix runs (swap ignored). A new draw path (e.g. per-chunk binds) bypassing the one call (private pipeline drawn with stock set 2 → shader reads unbound binding 13). |
| `TerrainBlastRenderer.BeforeNativeDispose` prefix | `PlanetRenderer.Dispose()` | `__instance` | Runs before the native device/renderer is torn down | Rename → load failure. If disposal moves (e.g. renderers stop being disposed individually) → owned Vulkan objects leak or are destroyed after the device. |
| `BlastSimulation.CapturePostfix` | `VehicleUpdateState.UpdateModules(SimStep)` | `__instance`, `step` | Runs **on physics workers**, after the nozzle states for *this* step are final. See §5 | Rename → load failure. If it moves before nozzle update, or onto the main thread, exposure is off by one step or is racy. |
| `Mod.DrainCommands` prefix (`Priority.First`) + `BlastSimulation.ApplyPostfix` postfix | `Universe.ApplyVehicleSolvers()` | none | Runs on the **main thread** after workers are joined and solver results applied, before the next dispatch. `JobSystems.WaitAll()` is safe to call in the prefix | If workers are still running here, or the method is renamed or split → samples applied out of order, or a cleared scene picks up old exposure. |
| `BlastPersistence.Capture` postfix | `GameSave.Populate()` (0 params) | `__instance`, `__runOriginal` | Called during save capture on the **same object** that is later `UncompressedSave.Write()`n (captures are keyed by instance in a `ConditionalWeakTable`) | If `UncompressedSave` stops deriving from `GameSave`, or Populate runs on a different object → "no matching blast snapshot", so the **sidecar is never written** (silent data loss on save). |
| `BlastPersistence.Written` postfix | `UncompressedSave.Write()` (0 params) | `__result` (bool), `__runOriginal` | `bool` success return; `Directory` (`DirectoryInfo`) is the save folder; `universe.xml` lives there (hashed by `BlastSaveCodec`) | Return-type change → binding failure. **Silent:** KSA defaulting to a **compressed** save format (only `UncompressedSave` is hooked), or renaming/moving `universe.xml`, means sidecars are never written or never validate. |
| `BlastPersistence.Loading` prefix / `LoadFinished` finalizer | `UncompressedSave.Load()` (0 params) | `__instance`, `__state`, `__exception` | `Universe.DeserializeSave` runs **nested inside** `Load()` (the sidecar read in the prefix is consumed by `AfterDeserialize`). `Program.IsEditorOpen` | If deserialization moves out of `Load()` (deferred, async, next frame) → saved marks never restored and the scene looks like a vanilla load. |
| `BlastPersistence.BeforeDeserialize` / `AfterDeserialize` | `Universe.DeserializeSave(UniverseData)` | `universeData`, `__runOriginal`, `__state` | Exact 1-param signature; KSA joins workers inside it | Signature/param rename → load failure. |
| `BlastPersistence.BeforeSystem` prefix | `Universe.LoadSystem(string)` | `id`, `__runOriginal` | New-system entry point; `SystemLibrary.Find(id)` | Param rename → load failure. A new scene-replacement path that skips both this and `DeserializeSave` → old marks leak into the new scene. |
| `Mod.DrawMenu` postfix | `Program.DrawProgramMenusHook()` | none | KSA calls it inside its main-menu-bar `BeginMenuBar` | Removed → load failure (not optional in Blasto). Called outside a menu bar → `BeginMenu` misbehaves. |
| `Mod.DrawHiddenUi` prefix | `Program.OnDrawUiConsole()` | none | Called every frame even when the UI is hidden; `Program.DrawUI` static | Removed → load failure. Semantics change → the window draws twice or never while the UI is hidden. |
| `Mod.BeforeGameDispose` prefix | `Program.Dispose()` | none | Runs **before** KSA destroys particles and the Vulkan device (Blasto frees GPU resources and kills emitters here) | If device teardown moves ahead of `Program.Dispose`, or into another method → use-after-free on exit. |
| `Mod.GuardTextInput` prefix | `GameSettings.OnKeyAll` | `__result` (`ref bool`) | `bool` return = "handled"; `Program.ConsoleWindow.IsOpen`; `ImGui.GetIO().WantTextInput` | Return-model change → binding failure, or typing in the tuning fields fires game hotkeys. |

Persistence patches use `Priority.Last` and only observe. They must keep coexisting with other
mods' save hooks (e.g. Unscience's).

### 3. Private terrain pipeline and shaders (highest risk)

Files: `Rendering/TerrainAccess.cs`, `TerrainBlastRenderer.cs`, `BlastRenderResources.cs`,
`BlastDescriptorLayout.cs`, `TerrainShaderSource.cs`, `TerrainBlastShaders.cs`,
`TerrainGpuCapabilities.cs`, `BlastGpuMark.cs`.

**Reflection (`TerrainAccess`).** These `PlanetRenderer` private fields are read and written
by name and type via `AccessTools.FieldRefAccess`:
`_renderer` (`Core.Renderer`), `_solid`, `_wire` (`VkPipeline`),
`_renderPipelineLayout` (`VkPipelineLayout`), `_renderShaderStages` (`ShaderStages`),
`_renderDescriptorSetLayout` (`DescriptorSetLayoutEx`),
`_cloudShadowsRenderer` (`KSA.Atmosphere.Rendering.CloudShadowsRenderer`) and
`_planetViewportData` (`PlanetViewportData[]`, indexed by `IViewport.ShaderSlot`).
A rename or type change throws in the static initializer, which is a load failure. A field that
keeps its name but **changes meaning** (e.g. `_solid` no longer used for the forward draw, viewport
data no longer indexed by shader slot) is silent.

**Descriptor set 2 clone (`BlastDescriptorLayout`).** Blasto rebuilds set 2 by hand as bindings
0–12 *copied from the native schema* plus binding 13 (fragment `StorageBuffer`).
- Runtime check: the native `_renderDescriptorSetLayout.Descriptors` must count **exactly** 5
  types: `UniformBufferDynamic`×2, `StorageBuffer`×2, `CombinedImageSampler`×2, `SampledImage`×5,
  `Sampler`×2. Any change refuses the private pipeline (stock kept).
- **Not checked at runtime: binding numbers, order, and stage flags** are hard-coded (0–1 UBO
  dynamic, 2–3 SSBO, 4–5 CIS, 6–10 sampled, 11–12 sampler, each with specific V/TC/TE/F/C stage
  masks). Diff the native set-2 layout creation in `PlanetRenderer` binding by binding. A swap
  that keeps the counts passes the check and produces an **incompatible layout**: Vulkan
  validation errors, or garbage bound to stock terrain inputs.
- Per-frame/per-view sets are filled by `VkCopyDescriptorSet` of bindings 0–12 from
  `Celestial.TextureSet`. This assumes the body's texture set **is** the native set 2 with that
  binding layout. Pool sizing in `PoolSizes` mirrors the counts.
- Slots: `ShaderSlot × Renderer.MaxFramesInFlight + frameIndex`, sized by
  `ViewportRegistry.MAX_VIEWPORTS`. This assumes `frameIndex < MaxFramesInFlight`, that a slot is
  host-writable once the native frame fence has retired it, and that `ShaderSlot` stays under
  `MAX_VIEWPORTS`. `TerrainBlastRenderer` keeps diagnostics for 8 slots.

**Pipeline layout (`BlastRenderResources` ctor).** Blasto recreates the forward layout as six
sets **in this order**: `GlobalShaderBindings.DescriptorSetLayout`,
`Program.Instance.TextureSystem.Layout`, private set 2,
`CloudShadowsRenderer.CloudShadowDescriptorSetLayout`,
`planet.GetSunShadowRenderer().TerrainShadowMap.MainSetLayout` and
`Program.GetCSMSystem().DescriptorSetLayout`. It adds one push-constant range of
`ByteSize.Of<PlanetViewportData.PushConstData>()` (128 bytes) for vertex | tess-eval | fragment.
Diff the native `_renderPipelineLayout` construction. A seventh set, a reorder, a different
push-constant size or stage mask, or a new shader-visible set → layout incompatibility at bind
time.

**Shader stages.** `_renderShaderStages.Count` must be 2 (no tessellation) or 4. Blasto replaces
the `VertexBit`, `FragmentBit` and (if 4) `TessellationEvaluationBit` modules and keeps the stock
tess-control module. A new stage, or the tess-control stage starting to forward outputs that
collide with location 7, needs work.

**Shader sources (`TerrainBlastShaders` / `TerrainShaderSource`).**
- `ModLibrary.Get<ShaderReference>("PlanetVert" | "PlanetFrag" | "PlanetTessEvaluation")`,
  `.ModPath` (an absolute source path read from disk at runtime), `.Stage`;
  `ShaderCompilerResolve.GetCompileOptionsWithIncludeCallback()` and
  `ShaderModuleUtils.FromString(device, src, stage, options, pathBytes)`. The include callback must
  keep resolving relative includes from the original path. An asset-id rename throws, so the
  build fails and stock terrain is kept.
- **Exact-text anchors** (each must occur **exactly once**, or the transform throws):
  - `Planet.frag`: `void main()`, `    // Lighting calculations`, `    outColor = finalColor;`.
    The injected `BlastoMaterial(blendedDiffuse, roughness, worldNormal)` also requires those
    three **variable names** at that point, and requires that they still mean albedo / roughness
    / normal **before** lighting is evaluated.
  - `Planet.vert`: `void main()`, `    outWorldPosition = worldPos;`.
  - `PlanetTessEvaluation.tese`: `void main(void)`, the 8-space-indented
    `\n        outWorldPosition = vec4(worldPos.xyz, 1.0f);` and the 4-space
    `\n    outWorldPosition = vec4(worldPos.xyz, 1.0f);`. It also relies on a local named
    `displacedPosition` holding the **displaced** surface point.
  - The fragment declarations add `layout(location = 7)` varyings and a `set = 2, binding = 13`
    std430 buffer. Location 7 and binding 13 must still be **unused** by the stock shaders.
- **Semantic (silent):** the meaning of `worldPos`, meaning a **camera-relative position in CCE
  orientation**. `BlastRenderResources.Prepare` matches it with
  `-viewport.GetCamera().GetPositionEgo(body)` rotated through `body.GetCce2Ccf()` and its inverse.
  If KSA changes the terrain shader's position space or camera-relative origin, marks slide,
  swim or vanish. Also check that lighting still follows the `// Lighting calculations` anchor,
  so marks stay lit natively.
- Offline check: `scripts/validate-shaders.sh <current-ksa>/Content/Core/Shaders` exports Blasto's
  exact transforms, then compiles and SPIR-V-validates both the standard and the
  `RAY_QUERIES_SUPPORTED` variants, and links the four stages. It knows about one pre-existing
  stock `planetNormalTexture` sampler mismatch and refuses any other baseline failure. If a stock
  change removes or alters that mismatch, update the script's expectation rather than the shaders.
  Also diff `Content/Core/Shaders/Planet/` and its includes directly between snapshots, because a
  passing compile doesn't prove the anchors still sit at the right *semantic* point.

**Device and Brutal Vulkan API.** `Renderer.{Device, Allocator, MaxFramesInFlight, GetLimits()}`,
`KSA.Rendering.Utils.MinStorageBufferOffsetAlignment`, `DescriptorSetLayoutEx.{Descriptors, CreateInfo}`,
`DescriptorPoolEx`, `BufferEx.{CreateInfo, Map, VkBuffer, BindOffset}`, `MappedMemory`,
`Device.{CreateDescriptorSetLayout, CreatePipelineLayout, CreateDescriptorPool, AllocateDescriptorSet,
UpdateDescriptorSets, WaitIdle, Destroy*}`. `TerrainGpuCapabilities` reinterprets limit fields that
Brutal exposes as **signed `int`** as `uint`. If Brutal changes those to `uint`, the constructor
call stops compiling (compile-caught), and the unsigned shim can be simplified.

**Distant bodies.** Marks render only when `Celestial.IsBillboarded()` is true (near terrain via
`PlanetRenderer`). `DistantSphereRenderer` is intentionally untouched. If KSA merges or reorganises
those paths, revisit.

Whole-feature diff list: `KSA/PlanetRenderer.cs` (fields, `BuildRenderPipeline`, `Render`,
`Dispose`, set-2 and pipeline-layout creation),
`KSA.Rendering.Planet/PlanetViewportData.cs` (`PushConstData`, `NearbyCelestial`),
`KSA/Celestial.cs` (`TextureSet`, `IsBillboarded`, `GetCce2Ccf`), `KSA/GlobalShaderBindings.cs`,
`KSA/ViewportRegistry.cs`, `KSA.Atmosphere.Rendering/CloudShadowsRenderer.cs`, the sun-shadow /
CSM systems, `RenderCore/ShaderModuleUtils.cs`, `RenderCore.Shaders/ShaderCompilerResolve.cs`,
`Brutal.VulkanApi/VkDeviceExtensions.cs` (`BindDescriptorSets`),
`Brutal.VulkanApi.Abstractions/DescriptorSetLayoutEx.cs`, plus `Content/Core/Shaders/Planet/**`.

### 4. Surface sampling and classification (game-state reads)

`Simulation/SurfaceProbe.cs`, `SurfaceProfiles.cs`:
- `Celestial.GetTerrainHeightFromDirCcf(double3 dir, accurate: true)`: radial height in **metres**
  including procedural modifiers. `Celestial.MeanRadius`, `Celestial.Id`.
  `BodyTemplate.OceanReference?.Level` (water exclusion).
- Classification: hard-coded body ids **`Luna`** and **`Mars`** (from `Content/Core/SolSystem.xml`).
  Then `BodyTemplate.TerrainReference?.BiomeMaterials?.Materials[]` `.BiomeId` / `.BiomeName`
  (substring-matched: rock/concrete/ice/snow, sand/soil/desert/dust), with
  `Celestial.SampleBiomeIdMap` / `SampleBiomeControlMap` (4-wide, `X..W`) and
  `GetAtmosphereReference()`. A renamed body or biome is **silent**: the surface just changes
  profile.
- Coordinate frames: everything is **CCF** (body-fixed), double precision.
  `Vehicle.GetPositionCce()` → `body.GetCce2Ccf()` for the diagnostic test mark;
  `Program.ControlledVehicle`, `Vehicle.Parent`. A frame-convention change (handedness,
  CCE↔CCF rotation direction, units) misplaces every mark silently.

### 5. Nozzle capture and the worker/main-thread contract

`Simulation/BlastSimulation.cs`. `Capture` runs on **physics workers** inside
`VehicleUpdateState.UpdateModules` and must only copy immutable data:
- `VehicleUpdateState.GetReadOnlyStates()` → `.Origin.Parent` (`Celestial`),
  `.GetStatesCcf(out pos, out _, out body2Ccf)`, `.Props.CenterOfMassAsmb`;
  `.UpdateData.RocketNozzles.GetReadOnlyStates()`; `.ReadOnlyVehicle.Id`.
- `RocketNozzleState`: `Performance.TotalThrust` (actual thrust, already including throttle and
  ambient conditions, so it is **not** multiplied by throttle again), `ThrustTime` (burn seconds
  within this step, clamped to `[0, DeltaTime]`), `ThrustLocationVehicleAsmb`,
  `ThrustDirectionVehicleAsmb` (exhaust = **negative** thrust direction; gimbal already applied).
  The position is `pos + (location − CoM).Transform(body2Ccf)`.
- `SimStep.DeltaTime` (steps > 1 s are skipped as warp), `SimStep.NextTime.Seconds()`.
- Main-thread side: `Universe.GetLastSimStep().NextTime` gates which queued samples are
  "applied". `Universe.CurrentSystem.All.TryGet(id, out Astronomical)`,
  `Universe.GetElapsedTime/GetElapsedSeconds()`, `JobSystems.WaitAll()`.

Silent risks: `ThrustTime` changing meaning (e.g. cumulative instead of per-step), thrust
location/direction changing frame (vehicle-asmb vs CoM-relative vs CCF), `GetStatesCcf` out-param
order changing (same types, so it still compiles), `UpdateModules` no longer running per nozzle
substep, or `ApplyVehicleSolvers` no longer being the post-join apply boundary. Diff
`KSA/VehicleUpdateState.cs`, `KSA/RocketNozzleState.cs`, `KSA/RocketNozzle.cs`,
`KSA/ActiveNozzle.cs` (burn-time clipping), `KSA/ReadOnlyPhysicsStates.cs`, `KSA/Universe.cs` and
`KSA/JobSystems.cs`.

### 6. Native dust particles

`Effects/LandingDust.cs`. An exception during emit sets `_failed`, and dust stays off for the
session while marks continue. API drift is therefore **Medium**, but semantic drift is invisible:
- `Program.Instance.ParticleSystem` → `.EmitterPool.Get(1, out acquired)`,
  `.InitializeEmitter(emitter, reference)`; `GameSettings.Current.Graphics.Particles`.
- `ParticleEmitter<ParticleUpdateData, ParticleRenderData>`: `.Context.{Astronomical,Vehicle,Part}`,
  `.Origin = new BubbleOrigin { Parent, Time, BubFrame = BubbleFrame.Ccf, PositionBub, VelocityBub }`,
  `.LocalOffset` (identity, because CCF spawning **rotates spawn positions by `LocalOffset`** in
  this build; a change double-rotates or un-rotates dust), `.SpawnRate`, `.Drag`, `.Opacity`
  (**the channel the volumetric renderer multiplies**; `Extra.z` is *not* opacity),
  `.ParticleInfo.ParticleSpawnLogic` (a delegate invoked on the **native particle worker**),
  `.CreateHandle()`, `.Kill()`; `Handle.TryGet()`; `Celestial.AddEmitter/RemoveEmitter`.
- `ParticleUpdateData.Create(pos, lifetime, size)` + `.Velocity/.Color/.Extra`.
- `ParticleEmitterReference` object-initializer fields (`Mesh`, `MaxParticles`, `SpawnMode`,
  `Renderer = ParticleRenderer.Volumetric`, `Updaters` = `SimpleMovement`, `GrowOverLifetime`,
  `ModelMatrix`, `Age`, `Density` (buoyancy, not opacity), `Drag`, `InheritVelocity`,
  `VehicleIsParent`, `EmitterRelative`, `ParticleLifespan/Size/Color`, `EmitterExtra`) and the
  `*Reference` wrapper types.
- Asset: `ModLibrary.Get<ParticleEmitterReference>("PlanetImpact").Get().ParticleEmitters[0].Get().Mesh`
  (only the sphere mesh is borrowed). Check `Content/Core/ParticleEmitterAssets.xml`.

Diff `KSA.Rendering.Particles/ParticleSystem.cs`, `ParticleEmitter.cs`,
`ParticleEmitterReference.cs`, `KSA/BubbleOrigin.cs`, and the particle shaders under
`Content/Core/Shaders/Particles/`.

### 7. UI (ImGui)

`Mod.Ui.cs`, `Mod.Tuning.cs`: `Brutal.ImGuiApi` `ImGui.{BeginMenu, MenuItem, EndMenu, Begin, End,
SetNextWindowSize, Checkbox, DragFloat(ImString, ref float, float, float, float, ImString,
ImGuiSliderFlags), SetItemTooltip, CollapsingHeader(ImString[, ImGuiTreeNodeFlags]), TextWrapped,
TextColored, SetClipboardText, GetIO().WantTextInput}`, `ImString` from UTF-8 literals,
`ImGuiCond`. Blasto relies on `ImGuiSliderFlags.None` **not** clamping typed values to the drag
range (`AlwaysClamp` semantics). If the Brutal binding changes the default, manual entry beyond the
drag range breaks silently. These calls are compile-caught except for that flag semantics.

### 8. Lifecycle (StarMap)

`Mod.cs`: `[StarMapMod]`, `[StarMapBeforeMain]`, `[StarMapAfterGui]` (`AfterGui(double dt)`),
`[StarMapUnload]`, and the `ImmediateUnload` property. StarMap.API is pinned at **0.3.6**, with
Lib.Harmony **2.4.2** as the NuGet fallback; local DLLs win when `STARMAP_DLL_DIR` is set.
`mod.toml` uses `[StarMap] EntryAssembly = "Blasto"`.
- **Ordering assumption:** `BeforeMain` runs **before** KSA builds terrain pipelines, so the
  `BuildRenderPipeline` postfix sees the first build. If KSA or StarMap moves pipeline creation
  earlier (or BeforeMain later), no private pipeline exists until a graphics rebuild. Marks are
  then absent, with "Private terrain pipeline unavailable" in diagnostics.
- Shutdown: `Program.Dispose` prefix (§2) does the real cleanup. `Unload` must stay an idempotent,
  managed-only no-op afterwards.

StarMap is the loader, not KSA, but StarMap bumps often ship alongside KSA builds, so check here.

### 9. Build references

`Directory.Build.props` resolves `$(KSAFolder)` as `-p:KSAFolder`, then `KSA_DLL_DIR`, then
`../ksa-game-assemblies/current/dll/`. `Blasto.GameMod.csproj` references (all `Private=false`,
**all `Condition="Exists(...)"`**): `BepuUtilities`, `BepuPhysics`, `KSA`,
`Brutal.Core.{Common,Logging,Numerics,Strings,Collections,Memory}`, `Brutal.Interop`,
`Brutal.Concurrency`, `Brutal.Render.Common`, `Brutal.ShaderC`,
`Brutal.Vulkan{,.Abstractions,.Vma}`, `Brutal.ImGui{,.Abstractions}`, `Planet.Core` and
`Planet.Render.Core`.

Because of the `Exists` conditions, a renamed DLL is **silently dropped** and shows up only as
missing-type errors. Check every name against `<current-ksa>/dll/`. **Known rename:** in KSA
**2026.10.19.5638**, Brutal renamed `Brutal.Core.Common` → `Brutal.Core` and
`Brutal.Core.{Logging,Numerics,Strings,Collections,Memory}` → `Brutal.{Logging,Numerics,Strings,Collections,Memory}`.
Namespaces were unchanged, so the csproj now lists **both** name sets (each `Exists`-conditioned)
and builds against 5554 and 5638 alike. Keep that pattern for future renames.

`scripts/package.sh` refuses to ship any `KSA.dll`, `Brutal.*`, `Planet.*`, `StarMap.API.dll` or
`0Harmony.dll`. Keep that intact if references change.

---

## Build and offline checks

```bash
# from the blasto repo root; point at the supplied snapshot's dll folder
KSA_DLL_DIR="<current-ksa>/dll" dotnet restore Blasto.slnx
KSA_DLL_DIR="<current-ksa>/dll" dotnet build Blasto.slnx -c Release --no-restore -m:1

# managed checks: no KSA refs at all, so they exercise none of the game coupling
dotnet run --project Blasto.Tests -c Release -p:KSAFolder=/missing

# shader anchors + SPIR-V validation + stage linking against the NEW stock shaders
bash scripts/validate-shaders.sh "<current-ksa>/Content/Core/Shaders"
```

`TreatWarningsAsErrors` is on. A clean build only means directly-referenced members still line up.
`Blasto.Tests` links only portable sources (`Core/`, `BlastSave*`, `TerrainShaderSource`,
`TerrainGpuCapabilities`, `BlastVisualMath`). It catches anchor drift through
`--export-shaders`, but nothing else game-side.

**Offline patch-apply harness (no game needed).** Build a throwaway console app in the scratchpad
with the same `Lib.Harmony` version as the csproj. Have it load the built `Blasto.dll` plus
`<current-ksa>/dll`, then call `harmony.Patch(...)` on every target listed in §2 with Blasto's
own hook methods. The easiest way is to invoke `TerrainBlastRenderer.Install`,
`BlastSimulation.Install` and `BlastPersistence.Install` via reflection. This exercises
`TerrainAccess` field resolution, Harmony parameter-name binding **and the `ReplaceForwardBinding`
transpiler** (which runs at patch time and enforces the single 6-arg bind). Gotchas:
- Simplest driver: `Activator.CreateInstance(Blasto.Mod)` then invoke `BeforeMain`, read the
  private `_message` (expect `Ready…`), and list `Harmony.GetAllPatchedMethods()` with
  `Harmony.GetPatchInfo` counts. Expect 14 targets; `Render` has prefix + transpiler + finalizer.
  `Unload`'s `JobSystems.WaitAll` throws an NRE offline (no game), which is harmless. Harmony also
  keeps listing unpatched methods (with zero patches).
- Add a `Microsoft.Extensions.Logging.Abstractions` 10.x package reference to the harness. The
  game install provides that DLL but the snapshot's `dll/` doesn't, and `BlastPersistence`'s
  patches fail to generate without it.
- Register an `AssemblyLoadContext.Default.Resolving` probe (Blasto bin dir + KSA dll dir)
  **before** any method that mentions KSA types is JIT-compiled. Keep the body in a separate
  `[MethodImpl(NoInlining)]` method.
- `Patch()` returns generated replacement methods (`DeclaringType == null`); use
  `Harmony.GetPatchInfo` / `GetPatchedMethods` to report targets.
- `KSA.dll` is **IL-only but flagged x64** (PE32+). On an arm64 host, copy the dll folder to
  scratch and set the copy's COFF `Machine` to `0xAA64`. Never modify the snapshot itself. Only
  patch files that have a CLR header (data directory 14, at optional-header offset 224 for PE32+ or
  208 for PE32); native DLLs such as `imgui.dll` must be left alone.

This dev host (ARM macOS) can't run the x64 game, so live checks (`VALIDATION.md` "In-game
acceptance": Luna test mark, tessellation on/off, multiple viewports, save/load, clean exit) must
be requested from the user. The in-game **Diagnostics → Copy diagnostics** report shows pipelines,
build failures, private binds per view and GPU limits. It is the fastest way to confirm a live
result.

## Severity triage

- **Critical**: semantic drift that lets the private pipeline **build and bind but mismatch** KSA:
  set-2 binding numbers/stages, pipeline-layout set order or push constants, the bind call's set
  range, `BuildRenderPipeline` destroying the old handles, or `Program.Dispose`/`PlanetRenderer.Dispose`
  no longer preceding device teardown. These damage **stock terrain or crash the game**, not just
  Blasto.
- **High**: anything that makes `BeforeMain` throw (a missing Harmony target or renamed parameter,
  a `TerrainAccess` field, transpiler shape). The whole mod is off, but loudly and safely. With no
  version guard, this is what users on an unreviewed build hit first. Also
  silent mark-placement drift (shader position space, CCE/CCF frames, nozzle frames or
  `ThrustTime` meaning) and **save-path drift** (sidecar not written or restored = lost marks).
- **Medium**: runtime schema checks that refuse the private pipeline (stock terrain kept; marks
  absent), shader-anchor drift caught by `validate-shaders.sh`, dust/particle API or semantics,
  `PlanetImpact` asset, the `ImGuiSliderFlags` clamp semantics, and the lifecycle-ordering change
  in §8.
- **Low**: build-ref renames with a mechanical fix, body/biome name changes affecting only
  surface profile, and docs text.

## Output format

Produce a review, not silent edits. For each affected touchpoint:

```
### <touchpoint> — <SEVERITY>
- KSA delta: <what changed between validated and current, with decomp file:line or version.json rev>
- Blasto location: <repo file:symbol>
- Coupling kind: <harmony | transpiler | reflection | render/behavioral | shader-anchor | game-state | worker-threading | particles | persistence | ui | lifecycle | build-ref | asset-id>
- Impact: <what breaks at compile/load/runtime, who notices (Blasto only vs stock terrain/game)>
- Change needed: <none | mechanical: … | behavioral redesign: …>
```

End with a **summary table** (touchpoint · severity · change needed) and an overall verdict:
*no changes needed* / *mechanical fixes only* / *behavioral work required*. If both
snapshots, the new `Content/` shaders, or the offline harness weren't available, state exactly
which checks you could not perform.

## Recording a validated build

After the review clears and the fixes land, all in one change:
1. `<Reference>` names in `Blasto.GameMod.csproj` if §9 found renames (add the new names; keep
   the old ones while older builds are still supported).
2. Pin lines in `README.md`, `INTEGRATION.md` and `VALIDATION.md`, plus the
   `BlastSaveDocument.GameBuild` default (used only when a document is built outside a save
   capture). Update `VALIDATION.md`'s automated results to what you actually ran against the new
   build, and don't carry over claims from the old one.
3. Rebuild, rerun `Blasto.Tests` and `validate-shaders.sh`, and run `bash scripts/package.sh Release`.
   Ask the user whether to bump the mod `Version` in the csproj and `mod.toml`.

## Maintaining this skill

When Blasto's coupling surface changes (a new Harmony patch, a new private field, a new shader
anchor, a new descriptor binding, a new game-state read or a dropped one), update this catalog
**and** `INTEGRATION.md` in the same change. The catalog is only useful while it mirrors the real
coupling exactly; a missing touchpoint here becomes a silent breakage at the next KSA bump. Grep
signals to re-derive the surface: `Patch(`, `HarmonyMethod`, `AccessTools.`, `FieldRefAccess`,
`ModLibrary.`, `Program.`, `Universe.`, `GameSettings.`, `Celestial`/`body.`, `ReplaceOnce(` in
`TerrainShaderSource.cs`, `VkDescriptorType`/`Binding(` in `BlastDescriptorLayout.cs`, the
`StarMap*` attributes in `Mod.cs`, and `<Reference Include=` in the csproj.
