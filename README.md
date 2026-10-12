# Blasto

Persistent landing-engine blast marks and dust for Kitten Space Agency. One StarMap mod, one shipping DLL, with no dependency on Unscience, gatOS or purrTTY.

The implementation targets **KSA 2026.10.10.5554** and StarMap API **0.3.6**. It builds and passes offline validation. Runtime diagnostics confirmed engine sampling and saved Lunar marks, but the terrain pipeline was rejected by a capability check. **Version 0.1.2 fixes incorrect signed comparisons of unsigned GPU limits**, which could reject a supported buffer range as negative. Live rendering still needs confirmation on this update. This ARM macOS development host cannot run the supplied x64 Linux game; see [VALIDATION.md](VALIDATION.md).

## Effects

- Persistent, irregular radial streaks blended into the terrain material before native lighting. Existing terrain texture, shadows and rocks remain visible.
- Every simulated rocket nozzle contributes actual thrust over its burn interval. Hovering and launches work as well as landings. Gimbal direction, ground slope and incidence shape placement.
- Double-precision positions keep marks fixed to a rotating body. Stable pattern seeds survive saving and loading.
- Lunar regolith, Martian dust and dry-soil profiles; conservative response on unknown atmospheric surfaces. Water is excluded. Earth soil classification uses the dominant terrain biome's name and is intentionally approximate.
- Short outward dust bursts use KSA's native volumetric particle renderer. Dust requires the game's particle setting; ground marks do not.
- The **Blasto → Landing effects…** menu controls effects, dust and surface response. It also offers a test mark beneath the controlled vehicle and a confirmed scene-clear action.

The visual model is artistic rather than a fluid or erosion simulation. It does not excavate terrain, remove clutter, shelter streaks behind individual rocks, weather old marks or emit water spray. Other mods that replace the same terrain shaders or pipeline may need a compatibility patch.

## Build and install

Requires .NET SDK 10 and reference DLLs from the pinned KSA build. The default lookup is the sibling `ksa-game-assemblies/current/dll` directory. Override it with `KSA_DLL_DIR` or `-p:KSAFolder=/path/to/game/dlls`. A local loader reference directory can be provided with `STARMAP_DLL_DIR`; otherwise restore uses the pinned StarMap and Harmony NuGet packages.

```sh
dotnet restore Blasto.slnx
dotnet build Blasto.slnx -c Release --no-restore -m:1
bash scripts/package.sh Release --no-restore -m:1
```

The package is `dist/Blasto/`, containing `Blasto.dll`, `Blasto.deps.json` and `mod.toml`. The script deliberately excludes game and loader DLLs.

1. Close KSA and place the **Blasto** folder in the active instance's `My Games/Kitten Space Agency/mods/` directory under Documents. `Content/Blasto/` is also supported by the loader.
2. Enable `Blasto` in that instance's mod manifest, then launch through StarMap. Avoid keeping a stale second copy under `Content/`, which takes precedence.
3. Open **Blasto → Landing effects…** and confirm the window says **0.1.2**. Start close to dry terrain on Luna or Mars; hover or descend with an active downward engine.

For a rendering check, click **Place test mark below vehicle**. It creates a strong 20 m radius site and enables **Highlight marks (diagnostic)**: a bright magenta terrain projection that ignores darkness and the normal height tolerance. The placement result updates after the next apply. Turn highlight off to inspect the natural streaks. If there is still no visible projection, **Copy diagnostics** captures the active pipelines, actual private descriptor binds, body, uploaded site count, distance, strength and exact GPU limits for troubleshooting. Highlight is temporary and is not saved.

Restart KSA after changing the DLL. Terrain integration refuses other game assembly versions; source anchors and descriptor checks also stop an incompatible shader variant from replacing stock rendering. Inspect `[Blasto]` log messages and the status window if marks do not appear.

## Saves and limits

Blasto writes `blasto.json` beside `universe.xml` after a successful native uncompressed save. It records sites and settings, uses an atomic replacement and binds the document to the native save's SHA-256. Loading a vanilla save or starting a new system clears the previous scene's effects. Invalid or incompatible data is reported; bounded original data is retained in `blasto.recovery.json` on the next successful save. Copy the whole save directory when moving a save.

- Up to **4,096 saved sites**; capacity rejects new locations instead of deleting history.
- At most **64 nearby marks per viewport**, selected within **5 km**. Dense overlapping sites and the distance boundary can visibly change the selected set; tiled accumulation is future work.
- Surface probes start within **350 m** of terrain. Physics steps longer than **one second** skip new exposure; the window reports these skips and existing marks persist.
- At most **12 dust emitters**, each capped at **32 particles**. This is a budget, not a measured performance guarantee.

Clearing marks affects the current scene; an existing disk save changes only when saved again. Disabling effects hides marks and stops new exposure without erasing them.

## Development

```sh
# Pure managed checks; game/native runtime is not needed.
dotnet run --project Blasto.Tests -c Release -p:KSAFolder=/missing

# Requires glslc and spirv-val; glslangValidator adds stage-interface checks.
bash scripts/validate-shaders.sh ../ksa-linux/Content/Core/Shaders
```

[INTEGRATION.md](INTEGRATION.md) records the native hooks, GPU contracts and ownership. [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) contains the source-backed research and future fidelity work. The modding corpus and Unscience informed rendering, physics and lifecycle choices; gatOS and purrTTY informed the single-mod packaging approach.
