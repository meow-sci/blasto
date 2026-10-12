# Lifecycle values and timing

The `lifecycle` and `hooks` frontmatter vocabulary used by every Mod Feature and How-To, with the frame structure of build 2026.10.10.5554.

KSA runs one main-thread frame per `Program.OnFrame` call and steps physics on worker threads
between frames, so a feature only works if it hooks the right moment. Every `Mod Feature` and
`How-To` concept carries a `lifecycle` list and a `hooks` list in frontmatter and a
`## Lifecycle and timing` body section that states where the code runs, what state is readable or
writable at that moment, and why the mod chose that point. Verify every claim against
`current/decomp/KSA/Program.cs`, `current/decomp/KSA/Universe.cs` and the StarMap patchers in
`StarMap.Core/Patches/`; do not infer timing from other engines.

```yaml
lifecycle: [frame:draw-ui, physics:vehicle-solver]        # values from the list below
hooks:                                                     # how the code attaches, one per hook
  - "starmap StarMapAfterGui"                              # StarMap attribute
  - "harmony-transpiler KSA.PhysicsStates.ComputeDerivatives"   # harmony-<prefix|postfix|transpiler|finalizer> <Type.Member>
  - "reflection KSA.Program._renderer"                     # reflection access to a private member
```

Frame structure of `Program.OnFrame(currentPlayerTime, dtPlayer)` in build 2026.10.10.5554, with the
lifecycle values to use:

| Value | What runs | Notes for modders |
|---|---|---|
| `startup:starmap-before-main` | `StarMapBeforeMain`, before `Program.Main` | No game state exists; only loader-level work. Harmony prefixes on `Program.Main` registered here (KittenExtensions, ShaderExtensions) also use this value |
| `startup:immediate-load` | `StarMapImmediateLoad(Mod)`, Harmony prefix on `Mod.PrepareSystems` | Per-mod, before systems and asset bundles load. Only a method with exactly one `KSA.Mod` parameter is called; a zero-argument one never runs |
| `startup:asset-load` | `ModLibrary.PreloadAssetBundles`, `LoadEditorTags`, `LoadAll`: mod.toml `assets`, XML asset bundles, then every `ILoader.Load` (file assets, shader compiles) on .NET thread-pool threads; KittenExtensions' XML patch run, a `ModLibrary.PrepareAll` prefix on the main thread that finishes before the first `startup:immediate-load` | Content mods live here; no code hooks except patches, and patches on loader paths must be thread-safe |
| `startup:all-mods-loaded` | `StarMapAllModsLoaded`, Harmony postfix on `ModLibrary.LoadAll` | Asset bundles are loaded but `ModLibrary.AttachGameData` has not run yet, so part templates do not have their game data merged; the universe/scene does not exist |
| `startup:game-loaded` | `Program.OnGameLoaded`, `Universe.OnLoaded` | Scene exists; typical place to resolve vehicles, celestials, statics |
| `frame:prepare` | `Program.PrepareFrame`: renderer rebuild, wait on job systems, apply solver results, poll input, dispatch next solvers, `Network.Tick` | The only main-thread window where physics results are settled, between the `Apply*Solvers` calls and the `ExecuteNext*Solvers` calls |
| `frame:apply-solvers` | `Universe.ApplyOrbitSolvers` / `ApplyVehicleSolvers` / `ApplyClothSolvers` inside `PrepareFrame` | Main-thread vehicle and orbit state receives the finished physics step; the worker's flight computer is copied back over the vehicle's with `FlightComputer.CopyFrom` |
| `frame:input` | `Glfw.PollEvents`, `InputEvents.ApplyInputEvents`, key dispatch (`GameSettings.OnKeyAll`) | Hotkeys, mouse; ImGui text focus interplay |
| `frame:dispatch-solvers` | `Universe.ExecuteNextClothSolvers` / `ExecuteNextVehicleSolvers` / `ExecuteNextOrbitSolvers` | Next physics step starts on worker threads; a prefix on `ExecuteNextVehicleSolvers` or on `Vehicle.PrepareWorker` (main thread, per vehicle, right before its snapshot) is the last settled point; main-thread mutation of simulated state after this is racy until the next `frame:apply-solvers` |
| `frame:imgui-new-frame` | `Program.PrepareImGui` (`ImGui.NewFrame`) | ImGui calls are legal from here until `frame:imgui-render` |
| `frame:viewports` | `OnFrameEditor`, `OnFrameViewports` (viewport and camera `OnFrame`) | Camera state for this frame is computed here |
| `frame:draw-ui` | `OnDrawUiFrame` (prefix = `StarMapBeforeGui`), `OnDrawUiViewports` (postfix = `StarMapAfterGui`), `OnDrawUiThreadSafe` (menu bar, vehicle windows, gauges, `Universe.OnDrawUi`), `OnDrawUiConsole` | Skipped entirely when the HUD is hidden (`DrawUI == false`); only `DrawFps` and `OnDrawUiConsole` still run in that state (mods postfix `DrawFps` or prefix `OnDrawUiConsole` to keep drawing; `StarMapBeforeGui` and `StarMapAfterGui` do not fire), and none of this runs on frames where `PrepareFrame` returns `Exit`; ImGui draw lists for sky/world overlays are built here |
| `frame:imgui-render` | `ImGui.Render` | Draw data is final; no more ImGui calls this frame |
| `frame:gauges` | `GaugeCanvas.UpdateCanvasesOnFrame` (before `PrepareImGui`), then after `ImGui.Render` `UpdateGauges`, `GaugeData.AnimateAll` | Cockpit gauge values |
| `frame:controller` | `OnFrameController` | Only syncs the cursor mode with the active controller; cameras actually move in `frame:viewports` |
| `frame:lights` | `LightSystem.ClearLights` / `OnFrame` | Light registration for this frame |
| `frame:celestials` | `OnFrameCelestials` (planet renderer `OnFrame`) | Terrain and planet streaming |
| `frame:pre-render` | `OnPreRender`: swapchain frame acquire, `Universe.OnPreRender` per viewport, `Editor.OnPreRender` | Per-frame render data has NOT been uploaded yet; `UpdateRenderingResources` / `Universe.UpdateRenderData` (vehicles, static objects, particles) run inside `Render`, so a postfix on one of those is `frame:render` |
| `frame:render` | `Render`: `Universe.UpdateWorldSun`, `UpdateRenderingResources` / `Universe.UpdateRenderData` (GPU buffers for this frame are filled), then `RenderGame` / `RenderEditor` Vulkan command recording (see render phases) | Custom pipelines and draws go into a specific phase; buffer updates happen here, not in `OnPreRender` |
| `frame:post-render` | `PostRender`: submit, present, `GizmoPass.PostRender` | Readbacks and screenshots |
| `frame:after-on-frame` | `StarMapAfterOnFrame`, Harmony postfix on `Program.OnFrame` | Everything above has run; the next physics step is already executing. Also fires on frames where `PrepareFrame` returned `Exit` (no UI, no render) |
| `physics:vehicle-solver` | Worker-thread vehicle step: `PhysicsBubble` jobs, `PhysicsStates.ComputeDerivatives` per substep (gravity, `ComputeDrag`, thrust, chutes), integration, Bepu constraint sim | Patches here run off the main thread and per substep; must be allocation-free and must not touch ImGui or Vulkan |
| `physics:orbit-solver` | Orbit propagation jobs (`JobSystems.OrbitSolvers`), one `CelestialUpdateTask` per celestial or orbiting star | Celestial bodies only; vehicle flight-plan and burn-plan prediction runs in the vehicle solver's post-work (`PhysicsBubble.RunVehiclePostWorkInner`) and is `physics:vehicle-solver` |
| `physics:cloth-solver` | Parachute cloth (`JobSystems.ClothSolvers`) | |
| `physics:bepu` | Bepu simulation inside the vehicle step (part trees, kittens, contacts) | Colliders, wheels, constraints |
| `render:<phase>` | One of `prepass`, `ambient-occlusion`, `shadows`, `planet`, `raytrace`, `scene-opaque`, `particles`, `ocean`, `atmosphere`, `exhaust`, `translucency`, `bloom`, `orbit-lines`, `gizmos`, `grid`, `gauges`, `aa`, `imgui`, `post-process` | Vocabulary only: the real order is interleaved (planet and shadow work alternate; gizmos, particles and ocean each record in two places; the final composite precedes the ImGui pass). Read the order from the game-loop reference in `content/howto/getting-started/lifecycle/game-loop-and-lifecycle/`. The base game has no generic post-process chain; `post-process` means passes a mod inserts around the final composite (ShaderExtensions) |
| `editor:<phase>` | Mod code that runs inside the vehicle editor's own entry points via a patch on them: `editor:on-frame` (`VehicleEditor.OnFrame`), `editor:draw-ui` (`VehicleEditor.OnDrawUi`), `editor:render` (`Program.RenderEditor`) | Only for patches on those methods; code that merely runs while the editor is open keeps its normal `frame:*` value |
| `thread:background` | A thread or task the mod owns | State the synchronization point back to the main thread |
| `event:<name>` | Game events the mod subscribes to or patches (`event:vehicle-destroyed`, `event:key`, `event:save`) | Free-form after the colon |
