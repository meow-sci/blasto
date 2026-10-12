# Validation

## Automated results

Validated locally against the supplied KSA `2026.10.10.5554` references and Linux shader assets:

- Debug and Release solution builds: **zero warnings and zero errors**.
- Managed console harness: **27/27 checks**, including unsigned GPU limit decoding (all five high-bit fields and the `0xffffffff` buffer range), supported/insufficient limit boundaries, terrain intersections, long grazing rays, non-finite terrain samples, strong diagnostic placement, stable pole frames, timestep-independent exposure, invalid input, compatible merging, saved-site capacity, body isolation, atomic state replacement, save integrity/version handling and diagnostic shader routing. It runs independently of the game runtime.
- Exact production transforms for vertex, tessellation evaluation and fragment shaders: **six successful compilations and SPIR-V validations**, covering standard and `RAY_QUERIES_SUPPORTED` configurations.
- Four-stage interface linking for both variants: passes after normalizing one unused sampler declaration **only in temporary validation copies**. Stock vertex/tessellation sources declare `planetNormalTexture` as different sampler types. The script first proves that exact stock baseline failure and refuses unrelated failures; shipped shaders are not normalized.
- Actual game DLL metadata audit: `PlanetRenderer.Render` contains the single six-parameter descriptor-bind call required by the transpiler, and the assembly version matches the guard.
- Package inspection: only the mod assembly, dependency manifest and StarMap manifest are shipped; no KSA, Brutal, Planet, Harmony or StarMap assemblies are bundled.

These checks do not execute Harmony in the game or create a live Vulkan pipeline. The ARM macOS host cannot run the supplied x64 Linux KSA distribution. User diagnostics from 0.1.1 confirmed successful Lunar surface sampling and deposition, but zero private pipelines/binds: the capability guard rejected the device before any mark upload. A source audit found that Brutal declares Vulkan's unsigned limit fields as signed C# integers, so a supported range with its high bit set fails the old signed comparison. Version 0.1.2 reinterprets those fields as unsigned, preserves genuine minimum checks, and includes all raw/unsigned/required values even if startup fails. Regression tests cover this exact failure mode. Live confirmation of rendering after this correction remains pending; shader sources are unchanged from the validated 0.1.1 variants.

## In-game acceptance

Use the pinned build, a known working StarMap installation and a test save. Record screenshots and frame-time comparisons before calling this release visually validated.

| Check | Expected result |
| --- | --- |
| Luna test mark, tessellation on/off | Feathered radial marks sit on terrain without floating, swimming or changing native lighting. |
| Hover, land, depart and return | Every active downward nozzle adds exposure; marks remain after vehicle departure. |
| Pause, resume, throttle cutoff, multi-engine vehicle | Pause adds no exposure; burns follow actual output; engines contribute independently. |
| Tilted engine and sloping terrain | Footprints follow the surface and extend with oblique incidence; no mark on a vessel or rock mesh. |
| Mars and Earth dry soil, shoreline and water | Mars uses its own tint/dust; atmospheric soil responds conservatively; ocean surfaces receive no mark. |
| Low sun/night and close/far camera | Native shadows remain coherent; dust is not emissive; streaks avoid edge sparkle. |
| Multiple viewports, camera switching and ray queries | No cross-view mark movement, binding validation errors or missing terrain. |
| Save/load, vanilla load, new system and repeated reload | Sites/settings restore exactly; stale scene marks and dust never leak into a new scene. |
| Malformed sidecar and unknown schema | Native load survives, UI reports the issue and bounded source data is retained for recovery. |
| Disable/re-enable, clear while burning, hidden UI | Controls work; disabled history stays saved; clear cannot replay old queued exposure. |
| Graphics/pipeline rebuild and clean game exit | Stock fallback works if private shaders fail; resources are released before device destruction. |
| 1, 16 and 64 nearby sites with dust on/off | Measure CPU/GPU frame time and particle cost; check visible selection changes at the budget boundary. |

Known fidelity limits are documented in README: no terrain excavation, rock sheltering, weathering, water effects or sparse texture-tile accumulation. CPU radial terrain sampling can differ from shader microdisplacement and needs visual checking on steep/detail-heavy surfaces.
