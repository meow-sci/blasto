# Native integration contract

Pinned to KSA `2026.10.10.5554`. Core accumulation, geometry, persistence encoding and shader transformation remain independently testable without loading native game libraries.

| Boundary | Implementation | Contract |
| --- | --- | --- |
| StarMap BeforeMain | `Mod.BeforeMain` | Install hooks before terrain pipelines are built; constructors do not access live game state. |
| Nozzle simulation | `VehicleUpdateState.UpdateModules` postfix | Copy immutable worker pose, gimballed nozzle geometry, actual thrust and clipped `ThrustTime`. No terrain/GPU mutation on workers. |
| Completed physics | `Universe.ApplyVehicleSolvers` postfix | Consume completed samples once; sample terrain, accumulate sites and create owned dust on the main thread. |
| Queued controls | Same method prefix | Join workers before clear/reset, preventing an old interval from entering the new capture generation. |
| Terrain pipelines | `PlanetRenderer.BuildRenderPipeline` postfix | Compile private stages and build matching private solid/wire pipelines. Restore native fields and cached shader modules even on failure. |
| Terrain drawing | `PlanetRenderer.Render` prefix/transpiler/finalizer | Temporarily select private pipelines/layout and substitute set 2 at the single native descriptor-bind call; restore native fields afterward. |
| Save capture/write | `GameSave.Populate` and `UncompressedSave.Write` postfixes | Snapshot managed state during capture; write only after that native save succeeds. Other mods' callbacks are untouched. |
| Scene replacement | `UncompressedSave.Load`, `Universe.DeserializeSave`, `Universe.LoadSystem` | Join workers and reset transient state; restore the sidecar in the native load scope. Missing bodies remain in saved history. |
| GPU cleanup | `PlanetRenderer.Dispose` and `Program.Dispose` prefixes | Wait for outstanding GPU work and free owned resources before the native device disappears. Later StarMap unload is idempotent. |

## Terrain data

The forward pipeline retains six descriptor sets and its 128-byte push constants. Private set 2 reproduces native bindings 0–12 and adds a fragment storage buffer at binding 13. Each native frame index and viewport slot has a distinct descriptor set and aligned buffer region; host writes occur after the native frame fence has retired the slot. Native dynamic buffer offsets are forwarded unchanged.

The storage layout is a 16-byte header followed by up to 64 records of 80 bytes. Runtime size/offset assertions guard the CPU layout. CPU positions and tangent frames are body-fixed doubles. Center-minus-camera subtraction happens before float packing using the native camera's precise relative origin. A dedicated location-7 varying carries the actual displaced surface through the tessellation evaluation stage without changing native lighting coordinates.

Capability checks must reinterpret the native bit patterns as unsigned. The pinned Brutal binding exposes Vulkan `uint32_t` limit fields as C# `int`; for example, `MaxStorageBufferRange` bits `0xffffffff` represent 4,294,967,295 bytes, not an invalid negative limit. `TerrainGpuCapabilities` handles that conversion and retains actual/required values in diagnostics before validation. Genuine insufficient limits are still rejected. [Vulkan limit field types](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceLimits.html).

The fragment variant multiplies the existing albedo and adjusts roughness and normals before native lighting. Multi-frequency spokes, broken noise and feathering share a saved seed. Angular filtering differentiates tangent coordinates before footprint rejection, avoiding divergent derivative use and the `atan` seam.

Pipeline creation, upload and source-anchor failures retain or restore stock terrain. No installed shaders, game assets, terrain height maps or stock particle definitions are modified. The per-fragment bounded loop still needs profiling at 64 visible sites.

## State and particle ownership

The spatial index merges compatible nearby footprints without changing their seed or tangent frame. Sites with substantially different radii remain separate so a concentrated low-altitude burn does not darken an earlier broad footprint. Exposure saturates; saved sites never silently evict at capacity.

Dust uses a private native emitter reference and only reuses the stock sphere mesh. Its CCF bubble origin anchors it independently of the vehicle. Owned native handles are registered with the body and retained until fenced pool cleanup invalidates them. Physical density and optical opacity are separate: mineral density preserves gravity; native opacity controls visible thickness. Flying particles are not persisted.

UI settings and terrain state belong to the active save. Shader resource lifetime spans scene loads; particle handles, queued samples and cached surface hits do not. Settings restoration cannot resurrect stale physics samples.
