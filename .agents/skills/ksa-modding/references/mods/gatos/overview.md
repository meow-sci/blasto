---
type: Mod
title: gatOS
description: A KSA mod that boots a real Alpine Linux in a QEMU microVM beside the game, exposes live telemetry and controls as a 9P /sim filesystem plus HTTP, MQTT, MCP and serial APIs, and adds programmable camera, paint, decal, audio and physics features on top
mod: gatos
tags: [qemu, linux, 9p, telemetry, http, mqtt, mcp, purrtty, vulkan, harmony, starmap]
repo: gatOS
license: MIT
author: meow sci
loader: starmap
mod_id: gatOS
---

gatOS ("gato" is Spanish for cat) gives a KSA spacecraft an onboard Linux computer. The mod boots a
tiny Alpine Linux guest in QEMU as a child process of the game, and players open real shell
sessions into it through the separate purrTTY terminal mod. Live vessel telemetry and controls are
mounted in the guest as files under `/sim`, so `cat`, `watch`, `tail -f`, `jq` and shell scripts
become the game API (`echo 1 > /sim/vessels/active/ctl/ignite`). The same data model is also served
over HTTP, MQTT, an MCP server for AI agents and an optional virtual serial port. Built by meow sci
(the purrTTY authors), MIT-licensed, 21 .NET projects plus Rust/TypeScript example clients and an
Astro documentation site ([README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/README.md), [AGENTS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/AGENTS.md)).

For modders the value is less the Linux novelty than the integration techniques: where in KSA's
frame to read and write vehicle state, how flight-computer writes must enter the solver step, how to
host processes and servers without touching the main thread, how to inject Vulkan draws, capture
frames, swap bindless textures and compile shaders at runtime, and how to keep a large mod alive
across frequent game updates.

## Install footprint

`mod.toml` ([gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml)): `name = "gatOS"`, `[StarMap] EntryAssembly = "gatOS.GameMod"`,
and one optional `[[StarMap.ModDependencies]]` on `purrTTY` with
`ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]`. No XML asset bundles.

| Location | Contents |
|---|---|
| mod folder `gatOS/` | `gatOS.*.dll` and NuGet DLLs (not `0Harmony`/`StarMap.API`), `mod.toml`, `gatOS.GameMod.deps.json`, `gatos.default.toml`, vendored `purrTTY.CustomShellContract.dll`/`purrTTY.Logging.dll`, `third-party-licenses/` |
| `gatOS/guest/` | `base.qcow2` (zstd qcow2 root), `vmlinuz-virt`, `initramfs-virt`, `manifest.toml` (boot contract and SSH host-key pin), `id_ed25519` session key |
| `gatOS/qemu/win-x64/` | trimmed portable QEMU (Windows release only; Linux uses system `qemu-system-x86_64`) |
| data folder `My Games/Kitten Space Agency/mods/gatOS/` | `gatos.toml` (live config), `disks/` (versioned bases and per-profile qcow2 overlays), `logs/` |

Releases ship `gatOS-windows-<v>.zip` and `gatOS-linux-<v>.zip`.

## Loader model and entry class

`gatOS.GameMod.Mod` is a `[StarMapMod]` `partial` class: a game-free half
([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs)) and a game-coupled half compiled only when KSA assemblies are present
([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs)).

| Hook | Attachment | What gatOS does there |
|---|---|---|
| `StarMapImmediateLoad` | StarMap (prefix on `Mod.PrepareSystems`) | never invoked: `OnImmediateLoad()` takes no parameter, and StarMap only registers `StarMapImmediateLoad` methods that take one `KSA.Mod` ([StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L72-L79)); the body is empty, so nothing is lost |
| `StarMapAllModsLoaded` | StarMap (postfix on `ModLibrary.LoadAll`) | game logging, fallback menu, asset validation, config, `/sim` tree and 9P server, HTTP, MQTT, MCP and mounts servers, VM host (no boot), purrTTY shell registration, solver/camera/display hooks |
| main-viewport `GameViewport.OnFrame` | Harmony prefix and postfix | telemetry sample, schedule tick, Frame-phase command drain, camera pose prep; postfix publishes the applied camera |
| `StarMapBeforeGui` | StarMap (prefix on `Program.OnDrawUiFrame`, skipped when `DrawUI` is false) | per-frame drivers: audio, paint/textures/stickers, thug-life anchors (and sample/drain if the viewport hook is absent) |
| `StarMapAfterGui` | StarMap (postfix on `Program.OnDrawUiViewports`, skipped when `DrawUI` is false) | welds and IVA physics drivers, each calling `JobSystems.VehicleSolver.Wait()` first because the step dispatched this frame may still be running; then the status window and modal |
| `StarMapAfterOnFrame` | StarMap (postfix on `Program.OnFrame`) | stand-in for both GUI hooks when the HUD is hidden, after that frame has rendered |
| `Universe.ExecuteNextVehicleSolvers` | Harmony prefix, `Priority.First` | Solver-phase drain (flight computer setpoints, refills, teleports) |
| `Program.DrawProgramMenusHook` | Harmony postfix | "gatOS" menu when ModMenu is absent |
| `Program.RenderGame` | Harmony transpiler | screen-stream capture before the frame's `End()` |
| `GameSettings.UiPixelCulling` | Harmony prefix | off while streaming |
| dynamic, only while in use | Harmony | `SuperMeshRenderSystem.RenderMainPass` postfix (thug_life), `RenderTarget.ResolveAttachments` postfix (stickers), `Vehicle.GetWorldMatrix`/`UpdateRenderData` prefixes (always_render), `PartModel` ctor postfix (always_render_iva), `ShaderModuleUtils.FromFile` prefix and `PartTreeRenderData.WriteState`/`WriteDynamicState` postfixes (paint) |
| `StarMapUnload` | StarMap (`ModLoader.Dispose` at host exit) | tear down cheats, bounded VM stop (15 s), dispose servers; its unpatch calls are no-ops because `StarMapCore.DeInit` has already run `Harmony.UnpatchAll()` for every mod ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)) |

The frame order these hooks sit in is documented in
[Frame hooks with an F2-proof fallback](features/frame-hooks-and-f2-fallback.md).

## Dependencies

- **StarMap** loader (StarMap.API 0.3.6) and **Harmony** (Lib.Harmony 2.4.2), both supplied to the mod
  load context by StarMap and not shipped.
- **purrTTY** (optional mod dependency) for the terminal UI; the contract assemblies are imported from
  purrTTY's load context, with vendored fallbacks ([vendor/purrTTY/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/vendor/purrTTY/README.md)).
- **ModMenu** (optional) via the `ModMenu.Attributes` 1.0.1 package; a Harmony fallback menu otherwise.
- NuGet: Tomlyn 2.6.0, SSH.NET 2026.0.0, MQTTnet 4.3.7, ModelContextProtocol.Core 2.2.0.
- Game assemblies referenced with `Private=false`: KSA, Brutal (Vulkan, FMOD, numerics, logging),
  Planet.Render.Core, BepuPhysics/BepuUtilities.
- Native/runtime: QEMU (bundled on Windows, system on Linux) and the pinned Alpine guest image
  (`guest/GUEST_VERSION` = 17).

## Architecture

```
game process (main thread)                         thread pool / tasks                 QEMU child process
  sampler -> SimSnapshot -- volatile swap -->  9P /sim, HTTP, MQTT, MCP, serial  <-- slirp TCP --  Alpine guest
  KsaCatalog <- CommandQueue.Drain <------- enqueue SimCommand, await result          (sshd, /sim mount,
  render hooks (quad, decals, capture)        VmHost, SSH.NET sessions  ------------->  /mnt mount, qga)
```

- **One read model, one write path.** A game-thread sampler publishes an immutable `SimSnapshot`;
  every transport projects it. Every write from every transport is an immutable `SimCommand` drained
  on the game thread in one of two phases (Frame or Solver) by `KsaCatalog`, the only KSA-aware router
  ([docs/ARCHITECTURE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/ARCHITECTURE.md)).
- **Only `gatOS.GameMod` references the game**; ten libraries (VM, SSH, 9P, SimFs, HTTP, MQTT, MCP, Bus,
  Paint, Logging) are game-free and unit-tested.
- **Threads.** Game state is read and mutated only on the main thread (sampler, drains, drivers, render
  hooks). 9P sessions, HTTP/MQTT/MCP servers, serial bridge, VM boot and SSH I/O run on the thread pool
  or dedicated threads and only touch snapshots and the queue.
- **Transport.** Everything host-guest is TCP over QEMU user networking, with ports passed on the
  kernel command line; no virtio-9p, virtiofs or vsock, because those lack Windows host backends.

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Compile-gated game seam](features/compile-gated-game-seam.md) | getting-started | `startup:all-mods-loaded` |
| [Frame hooks with an F2-proof fallback](features/frame-hooks-and-f2-fallback.md) | getting-started | `frame:viewports`, `frame:draw-ui`, `frame:after-on-frame` |
| [Logging shim and perf stats](features/logging-and-perfstats.md) | getting-started | `startup:all-mods-loaded`, `frame:draw-ui` |
| [Solver-phase drain for flight computer setpoints](features/solver-phase-command-drain.md) | patching | `frame:dispatch-solvers` |
| [9P2000.L server in C#](features/ninep-server-in-csharp.md) | external-io | `startup:all-mods-loaded`, `thread:background` |
| [Embedded MQTT broker](features/embedded-mqtt-broker.md) | external-io | `startup:all-mods-loaded`, `thread:background`, `event:unload` |
| [External API clients and SDK](features/external-api-clients.md) | external-io | `thread:background` |
| [Game-thread command queue](features/game-thread-command-queue.md) | external-io | `frame:viewports`, `frame:draw-ui`, `frame:after-on-frame`, `frame:dispatch-solvers`, `thread:background` |
| [HTTP /v1 API server](features/http-v1-api-server.md) | external-io | `startup:all-mods-loaded`, `thread:background`, `event:unload` |
| [Host-guest transport over slirp TCP](features/slirp-tcp-host-guest-transport.md) | external-io | `startup:all-mods-loaded`, `thread:background` |
| [MCP server for AI agents](features/mcp-server-for-agents.md) | external-io | `startup:all-mods-loaded`, `thread:background`, `event:unload` |
| [QEMU microVM as a game child process](features/qemu-microvm-host-process.md) | external-io | `startup:all-mods-loaded`, `thread:background`, `event:unload` |
| [SSH shell sessions into the guest](features/ssh-shell-session-into-guest.md) | external-io | `thread:background` |
| [Serial bus with CCSDS, NMEA and SCPI](features/serial-bus-ccsds-nmea-scpi.md) | external-io | `thread:background` |
| [Snapshot-diff flight events](features/snapshot-diff-flight-events.md) | external-io | `frame:viewports`, `frame:draw-ui`, `thread:background` |
| [Telemetry sampler and snapshot store](features/telemetry-sampler-snapshot.md) | external-io | `frame:viewports`, `frame:draw-ui`, `frame:after-on-frame`, `thread:background` |
| [The /sim file models](features/sim-filesystem-file-models.md) | external-io | `thread:background` |
| [Timed command scheduler](features/timed-command-scheduler.md) | external-io | `frame:viewports`, `frame:draw-ui`, `thread:background` |
| [purrTTY custom shell contract](features/purrtty-custom-shell-contract.md) | external-io | `startup:all-mods-loaded` |
| [Debug cheats for teleport, refuel, warp and impulse](features/debug-cheats-teleport-refuel-warp.md) | flight-and-orbits | `frame:dispatch-solvers`, `frame:viewports`, `frame:draw-ui` |
| [Vessel flight-state reads](features/vessel-flight-state-reads.md) | flight-and-orbits | `frame:viewports`, `frame:draw-ui` |
| [Ignition, staging and module actuators](features/ignition-staging-and-module-actuators.md) | vehicles-and-parts | `frame:viewports`, `frame:draw-ui`, `frame:input` |
| [Manual throttle and RCS inputs](features/manual-throttle-and-rcs-inputs.md) | vehicles-and-parts | `frame:viewports`, `frame:draw-ui`, `frame:dispatch-solvers` |
| [Part and module state reads](features/part-module-state-reads.md) | vehicles-and-parts | `frame:viewports`, `frame:draw-ui` |
| [Celestial body catalog reads](features/celestial-body-catalog-reads.md) | celestials | `frame:viewports`, `frame:draw-ui` |
| [IVA cabin physics in an own Bepu world](features/iva-cabin-physics-bepu.md) | physics | `frame:draw-ui`, `frame:after-on-frame` |
| [Welds that pin one vessel to another](features/welds-vessel-anchoring.md) | physics | `frame:draw-ui`, `frame:after-on-frame` |
| [FX editors via reflection](features/fx-editor-reflection.md) | rendering | `frame:viewports`, `frame:draw-ui` |
| [Ground-clutter texture override](features/clutter-texture-override.md) | rendering | `frame:viewports`, `frame:draw-ui`, `frame:after-on-frame` |
| [Part light cone control](features/light-cone-control.md) | rendering | `frame:viewports`, `frame:draw-ui`, `frame:render` |
| [Particle bursts from the emitter pool](features/face-particle-bursts.md) | rendering | `frame:viewports`, `frame:draw-ui`, `frame:pre-render`, `render:particles` |
| [Per-vessel scale and force-render cheats](features/scale-and-force-render-cheats.md) | rendering | `frame:viewports`, `frame:draw-ui`, `frame:render` |
| [Projected sticker decals](features/sticker-projected-decals.md) | rendering | `render:grid`, `frame:viewports`, `frame:draw-ui`, `frame:after-on-frame` |
| [Screen stream frame capture](features/screen-stream-frame-capture.md) | rendering | `render:post-process`, `thread:background` |
| [World-space textured quad (thug_life)](features/thug-life-world-space-quad.md) | rendering | `render:scene-opaque`, `frame:draw-ui`, `frame:after-on-frame` |
| [Vehicle and EVA paint](features/vehicle-and-eva-paint.md) | shaders | `frame:prepare`, `frame:viewports`, `frame:draw-ui`, `frame:after-on-frame`, `frame:render` |
| [Programmable camera director](features/programmable-camera-director.md) | camera | `frame:viewports` |
| [Synthetic camera follow anchor](features/synthetic-camera-follow-anchor.md) | camera | `frame:viewports` |
| [FMOD audio playback](features/fmod-audio-playback.md) | audio | `frame:viewports`, `frame:draw-ui`, `frame:after-on-frame`, `frame:post-render`, `thread:background` |
| [ModMenu entry with a fallback menu](features/modmenu-entry-with-fallback-menu.md) | ui | `startup:all-mods-loaded`, `frame:draw-ui` |
| [TOML config seeded from a shipped template](features/toml-config-seeded-template.md) | persistence | `startup:all-mods-loaded`, `thread:background` |
| [qcow2 overlay guest disks](features/qcow2-overlay-guest-disks.md) | persistence | `thread:background` |
| [Alpine guest image pipeline](features/alpine-guest-image.md) | testing-and-tooling | `thread:background` |
| [Build, deploy and CI](features/build-deploy-and-ci.md) | testing-and-tooling | build time |
| [KSA anchor churn tracking](features/ksa-anchor-churn-tracking.md) | testing-and-tooling | `frame:viewports`, `frame:draw-ui` |

## Build, test and tooling

- `dotnet build` builds the 21-project solution and deploys the complete mod folder; `dotnet test`
  runs the NUnit suites (about 1650 tests at the 5482 pass); `GATOS_IT=1` adds real-VM integration
  tests, which CI runs under KVM ([.github/workflows/build.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/build.yml)). See
  [Build, deploy and CI](features/build-deploy-and-ci.md).
- The guest image is built by `guest/build-image.sh` and published as `guest-v<N>` releases.
- Every KSA binding carries a `[KsaAnchor]` attribute; `scope/` and `docs/KSA_INTEGRATION_MATRIX.md`
  are the break-impact catalog; an `upgrade-ksa` agent skill encodes the upgrade playbook.
- Agent skills under `.agents/skills/` (ksa, gatos, harmony, imgui, mcp, tutorials ...) document the
  game and the API for coding agents.

## Compatibility notes

- The repo's last audited KSA build is 2026.9.22.5482 (pinned commit dated 2026-09-25). This bundle
  checked every game member it cites against build 2026.10.10.5554; all were present. Notable 5554
  differences in cited code: `RenderGame` gained a KSA screenshot-capture call before the AA passes,
  and line positions moved throughout.
- Breakage history recorded in the repo: the `KSA.Viewport` deletion (5402), Vulkan dynamic rendering
  replacing `Program.OffScreenPass` (5154), `UniverseTime` replacing `SimTime` (5211), the cached
  `PartTreeRenderData` (5456/5482), flight-computer and staging semantic changes (5329, 5143, 5128).
  Render internals, reflection targets and private members are the highest-churn couplings.
- Many in-game validation checklists (MCP, paint, stickers, camera recheck, IVA, audio) are marked as
  pending in `docs/VALIDATION.md`.
- purrTTY must be rebuilt alongside gatOS when KSA render internals change; an outdated purrTTY can
  crash in ways that look like gatOS failures.

## Secondary sources

| Document | Why read it |
|---|---|
| [AGENTS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/AGENTS.md) | project map, threading rules, the nine game-thread work sites, per-build upgrade notes |
| [docs/ARCHITECTURE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/ARCHITECTURE.md) | runtime shape, port allocation, telemetry pipeline, render injections |
| [docs/KSA_INTEGRATION_MATRIX.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/KSA_INTEGRATION_MATRIX.md) | every `/sim` path mapped to its KSA member, risk and phase |
| [docs/KSA_CELESTIAL_COORDINATE_FRAMES.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/KSA_CELESTIAL_COORDINATE_FRAMES.md) | KSA's ECL, ORB, CCE, CCF and CCI frames |
| [docs/PARTICLE_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/PARTICLE_ANALYSIS.md) | particle system internals and three tiers of control |
| [docs/LIGHT_PART_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/LIGHT_PART_ANALYSIS.md) | how spotlights are built and rendered, cone swap and falloff floor |
| [docs/TUTORIAL_DATA_REFERENCE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/TUTORIAL_DATA_REFERENCE.md) | flight-program facts: phases, frames, pacing in sim time |
| [docs/VALIDATION.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/VALIDATION.md) | in-game validation record and pending checklists |
| [scope/FULL_SCOPE.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/scope/FULL_SCOPE.md) | feature inventory and the game-update break-check playbook |
| [spike/NOTES.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/spike/NOTES.md) | Linux 9p (netfslib) file semantics learned against a real guest |
| [OS_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/OS_ANALYSIS.md) | why QEMU, and why TCP instead of virtio-9p/virtiofs/vsock |
| [SPEC_9P_FILESYSTEM.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/SPEC_9P_FILESYSTEM.md) | the published `/sim` API |
| [SPEC_MCP.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/SPEC_MCP.md) | the MCP contract |
| [plans/PAINT_ASBUILT.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/plans/PAINT_ASBUILT.md) | paint seam and maintenance contract |
| [plans/CAMERA_ASBUILT.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/plans/CAMERA_ASBUILT.md) | programmable camera as built |
