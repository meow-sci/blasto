# Blasto

Persistent rocket-exhaust blast marks and dust for Kitten Space Agency. One StarMap mod, one shipping DLL, with no dependency on Unscience, gatOS or purrTTY.

The implementation targets **KSA 2026.10.10.5554** and StarMap API **0.3.6**. User diagnostics and screenshots confirm working Lunar marks in 0.1.2. **Version 0.1.3 makes those marks substantially larger and darker and adds eleven live tuning controls.** It builds and passes offline validation; the new appearance still needs in-game visual confirmation. This ARM macOS development host cannot run the supplied x64 Linux game; see [VALIDATION.md](VALIDATION.md).

## Effects

- Persistent, irregular radial streaks blended into the terrain material before native lighting. Existing terrain texture, shadows and rocks remain visible.
- Every simulated rocket nozzle contributes actual thrust over its burn interval. Hovering and launches work as well as landings. Gimbal direction, ground slope and incidence shape placement.
- Double-precision positions keep marks fixed to a rotating body. Stable pattern seeds survive saving and loading.
- Lunar regolith, Martian dust and dry-soil profiles; conservative response on unknown atmospheric surfaces. Water is excluded. Earth soil classification uses the dominant terrain biome's name and is intentionally approximate.
- Short outward dust bursts use KSA's native volumetric particle renderer. Dust requires the game's particle setting; ground marks do not.
- The **Blasto → Landing effects…** menu controls effects, surface appearance, exposure buildup and dust. Its collapsed Diagnostics section offers a test mark beneath the controlled vehicle and a copyable report. Scene clearing requires confirmation.

The visual model is artistic rather than a fluid or erosion simulation. It does not excavate terrain, remove clutter, shelter streaks behind individual rocks, weather old marks or emit water spray. Other mods that replace the same terrain shaders or pipeline may need a compatibility patch.

## Tuning and liftoff

All eleven controls use DragFloat with suggested mouse-drag ranges. Double-click or Ctrl-click to enter values outside those ranges; save/load preserves them. Only invalid signs, non-finite values and values unrepresentable by the game renderer are rejected. Settings are saved with the scene.

| Control | Default | Suggested drag range | Effect |
| --- | ---: | --- | --- |
| Size multiplier | 3 | 0.1–10 | Scales the radius of existing and future marks, preserving the stored footprint. |
| Visibility | 8 | 0–30 | Makes weak saved exposure visible; zero hides normal mark appearance. |
| Darkness | 3 | 0–10 | Deepens the terrain color while retaining texture and native lighting. |
| Streak strength | 2 | 0–8 | Strengthens radial rays. |
| Center strength | 1 | 0–4 | Adjusts the concentrated central burn. |
| Edge softness | 1 | 0.05–3 | Larger values soften the footprint boundary. |
| Buildup rate | 3 | 0–20 | Multiplies exposure accumulated during future burns; zero stops mark buildup. |
| Erosion threshold (Pa) | 0 | 0–100 | Pressure subtracted before future erosion; lower values admit weaker burns. |
| Dust opacity | 1 | 0–5 | Scales opacity of active emitters and new dust. |
| Dust size | 1 | 0.1–5 | Scales the size of newly emitted particles. |
| Dust speed | 1 | 0–5 | Scales the velocity of newly emitted particles. |

**Liftoff, hovering and landing use the same exhaust calculation.** There is no ascending/descending velocity gate. Surface exposure depends on actual nozzle thrust, ground incidence, footprint area and burn duration: `response × max(thrust × incidence / area − threshold, 0) × burnSeconds / 1800 × buildupRate`. The default threshold is now zero rather than 15 Pa. Rapid departure spends less time close to the surface than a hover or slow descent, so it can leave less exposure. Exhaust must still reach dry terrain within the sampling range.

Size and visibility apply immediately to old sites without multiplying their saved radius or exposure. Old saves receive the new visual defaults and retain their explicitly saved surface-response value as the buildup rate. **Reset tuning defaults** also sets buildup to 3 while preserving enable switches. Dust has separate controls and can continue when buildup is zero; its small emission threshold and particle budgets still apply.

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
3. Open **Blasto → Landing effects…** and confirm the window says **0.1.3**. Start close to dry terrain on Luna or Mars; launch, hover or descend with an active downward engine.

For a rendering check, expand **Diagnostics** and click **Place test mark below vehicle**. It creates a strong site with a stored radius of 20 m (60 m displayed at the default size multiplier) and enables **Highlight marks (diagnostic)**: a bright magenta terrain projection that ignores darkness and the normal height tolerance. The placement result updates after the next apply. Turn highlight off to inspect the natural streaks. If there is still no visible projection, **Copy diagnostics** captures the active pipelines, actual private descriptor binds, body, uploaded site count, distance, strength and exact GPU limits for troubleshooting. Highlight is temporary and is not saved.

Restart KSA after changing the DLL. Blasto does not check the game version; it attempts to run on any KSA build. Missing patch targets stop initialization, and source anchors and descriptor checks stop an incompatible shader variant from replacing stock rendering. Inspect `[Blasto]` log messages and the status window if marks do not appear.

## Saves and limits

Blasto writes `blasto.json` beside `universe.xml` after a successful native uncompressed save. It records sites and settings, uses an atomic replacement and binds the document to the native save's SHA-256. Loading a vanilla save or starting a new system clears the previous scene's effects. Invalid or incompatible data is reported; bounded original data is retained in `blasto.recovery.json` on the next successful save. Copy the whole save directory when moving a save.

- Up to **4,096 saved sites**; capacity rejects new locations instead of deleting history.
- At most **64 nearby marks per viewport**, prioritized by estimated visible contribution (size, exposure and distance). Footprints reaching within **5 km** are eligible. Dense overlapping sites and the distance boundary can visibly change the selected set; tiled accumulation is future work.
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
