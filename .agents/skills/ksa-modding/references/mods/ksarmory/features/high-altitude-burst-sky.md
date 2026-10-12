---
type: Mod Feature
title: What a high or airless burst lights in the sky, chosen by the air at the burst
description: The air density at a nuclear burst picks which sky effects it starts, each drawn as an analytic full-screen compute dispatch tagged by a negative bound in the cloud pass, and an airless body gets one-shot KSA particle emitters instead
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - glsl-shader
  - post-processing
  - xml-asset
  - push-constant-kind-tag
  - per-kind-dispatch-cap
  - dipole-field-model
  - column-calibrated-layers
  - pooled-resource
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.SunbloomRenderer.Render
lifecycle:
  - startup:all-mods-loaded
  - frame:draw-ui
  - frame:celestials
  - frame:pre-render
  - frame:render
  - render:particles
  - render:bloom
game_types:
  - KSA.SunbloomRenderer
  - KSA.Program
  - KSA.Camera
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.AtmosphericBody
  - KSA.PhysicalAtmosphereReference
  - KSA.Universe
  - KSA.BubbleOrigin
  - KSA.BubbleFrame
  - KSA.ExplosionSystem
  - KSA.GameSettings
  - KSA.Rendering.Particles.ParticleSystem
  - KSA.Rendering.Particles.ParticleEmitter
  - KSA.Rendering.Particles.EmitterSpawnMode
tags:
  - nuclear
  - high-altitude
  - aurora
  - airglow
  - x-ray
  - debris-shell
  - magnetic-field
  - red-wave
  - vacuum
  - particles
  - compute-shader
  - analytic-shading
---

A nuclear burst high in KSArmory's sky does not grow a mushroom. Depending on how thin the air is
where it went off, it lights the air 80-odd km up with its X-rays, throws its debris along the
planet's magnetic field to light an aurora at both ends of the field line, leaves a glowing shell
of debris (or, above the air, a field-held bubble hundreds of kilometres across), and sends a red
wave out through the thermosphere. Each of those is a small game-free model in `Sim/` and one
**full-screen analytic compute dispatch** recorded in the mod's cloud pass before bloom, told apart
from a cloud by a negative bounding radius in the push constants.[^ksarmory-nuclearclouds][^ksarmory-cloud-comp]

On a body with no air there is no sky to light and no buoyant cloud to raise, so the same entry
point hands over to two **one-shot KSA particle emitters**: a glowing debris shell and a dome of
thrown ground that KSA's own particle physics carries on ballistic arcs.[^ksarmory-burstejecta]
The compute pass these dispatches ride on is documented in
[compute-pass-before-bloom](./compute-pass-before-bloom.md); this file covers what the sky adds on
top of it.

## How it works

### 1. The air at the burst picks what is started

Every burst enters through `NuclearClouds.Begin(burstEcl, near, chargeKg, ...)`, which returns at
once below `MushroomCloud.ThresholdKg` (1,000 kg TNT).[^ksarmory-mushroomcloud] It reads two things
from the body:

- `hasAir = KsaWorld.HasAtmosphere(body)`: the body's `GetAtmosphereReference()?.Physical` exists
  and its `Height` is positive (NuclearClouds.cs L869).[^ksarmory-ksaworld][^ksa-astronomical]
- `airRatio = KsaWorld.AirDensityRatioAt(body, burstEcl)`: KSA's
  `PhysicalAtmosphereReference.GetAtmosphericDensityAtAltitude` (an isothermal
  `SeaLevelDensity * exp(-h / ScaleHeight)`) over a fixed 1.225 kg/m3, **zero at or above the
  declared boundary `Height`** and zero with no physical atmosphere, but **1.0 if the read
  throws** (KsaWorld.cs L2493-L2543).[^ksa-physicalatmosphere]

A burst inside one still burning on the same body (`MushroomCloud.IsTheSameBurst`) is merged first
(`JoinTheSameBurst`, L889): its charge is added to that burst's glow, wave, curtains and cloud, so a
six-warhead bus landing in one frame draws one sky, not six.[^ksarmory-nuclearclouds]
Then the thresholds, all fixed ratios to sea-level air. The altitudes are derived here for KSA's
Earth (1.225 kg/m3, 8 km scale height, Astronomicals.xml L1336-L1339, boundary about 167 km where
`CalculateBoundaryHeight` takes the larger of density 1e-9 and pressure 1e-4 Pa):[^ksa-astronomicals-xml][^ksa-physicalatmosphere]

| Test (code) | Air ratio | KSA Earth | What it starts |
|---|---|---|---|
| `!hasAir` | none | Moon-like bodies | `BurstEjecta.Begin` (section 5), a ground mark if on land; no cloud, no sky |
| `MushroomCloud.IsThin` | `< 0.01` | above ~37 km | The cloud entry is "thin": skipped by the mushroom march, drawn as a debris shell (kind 2) and removed at `DebrisShell.LifeSeconds` (180 s); no drawn shock front |
| `RedWave.Lights` | `> 0` and `<= 2.5e-4` | ~66 km up to the boundary | A red wave, if `Config.RedWave` |
| `Aurora.Lights` | `< 1e-4` | above ~74 km | Up to two auroral curtains, one at each end of the burst's field line |
| `XRayGlow.Lights` and `Strength(kt, over, 1 s) > 0.002` nits | `< 1e-5` | above ~92 km | The X-ray-heated layer, unless so far out it would not show |
| field pressure `>= ` air pressure (`DebrisBubble.FieldShare >= 0.5`) | n/a | about 150 km (author's figure) | The debris is drawn as `DebrisBubble` instead of `DebrisShell` |

A burst above the boundary reads air 0: it is thin, lights the glow and the aurora, gets the
bubble, and sends **no** red wave (that needs some air to heat).[^ksarmory-redwave]

### 2. The four sky kinds

The heights in each model are derived from the body's own air (`BodyAir`, built by
`KsaWorld.BodyAirOf` from KSA's declared sea-level pressure, density, scale height and boundary,
plus the mod's `Bodies.xml` traits). A layer is placed where the **column of air above it**
(`density x scale height`) reaches a value calibrated on KSA's Earth, so it moves sensibly on another
body (`BodyAir.AltitudeOfColumn`).[^ksarmory-bodyair]

| Kind (`FireSun.z`) | Model | Look and life | Push constants (beyond `InvViewProj`) | Shader |
|---|---|---|---|---|
| 0 Glow | `XRayGlow`: layer at the column for 82 km on Earth (over `XRayOpacity`), 2.5 scale heights thick | `0.3 nits x (kt/1000) x (100 km / height over layer)^2`, capped 0.5; rises over 1 s, red e-folds 110 s, gone at 600 s; green share `exp(-age/3)`[^ksarmory-xrayglow] | `CentreRadius` = burst (camera-relative), `-nits`; `FireSun` = layer altitude, thickness, 0, 0; `Shape.x` = green share | `DrawXRayGlow`: the ray's spans between two spheres, 8 taps each, inverse square from the burst, skipped where the planet shadows the burst; capped at `LimbGain` 4 |
| 1 Aurora | `Aurora`: a dipole on the spin axis, `r = L R cos^2 lat`; `Footpoints` solves where the burst's line meets the foot altitude (column for 100 km), own hemisphere first; top 37.5 scale heights higher | `0.25 nits x sqrt(kt/1000)`; arrives over 3 s, e-folds 240 s, gone at 900 s[^ksarmory-aurora] | `CentreRadius` = foot on the mean sphere, `-nits`; `FireSun` = bottom, top, 1, sine of the foot's magnetic latitude; `Shape` = magnetic east, age | `DrawAurora`: four noisy arcs standing on field lines, the ray cut to a slab tilted with the field, 6-48 steps of at most 20 km, each sheet crossing found between steps; oxygen green, nitrogen pink, oxygen red haze by height |
| 2 Debris | `DebrisShell` (field does not dominate) or `DebrisBubble` (it does) | Shell: the ball's glow or a red afterglow (4 units, e-fold 40 s), white-hot fill fading to a rim, stretched up to 2.5x along the field as the air thins; 180 s.[^ksarmory-debrisshell] Bubble: equal-volume radius `cbrt(3 mu0 f E / 2 pi B^2)`, `f = 0.025` fitted on Starfish, stretched 2.7x, grown in 1.2 s, collapsed by 16 s; with no field, ballistic for 4 s[^ksarmory-debrisbubble] | `AgeStrengthWind` = colour, fill; `CentreRadius` = centre, `-radiance`; `FireSun` = radius, stretch, 2, clip altitude (0 = shell, X-ray layer altitude = bubble); `Shape` = field direction, age | `DrawDebris`: shell mode 32 taps through the stretched sphere; bubble mode the rim in closed form, two crossings with one noise lookup each, eased in over 30 km above the clip |
| 3 Red wave | `RedWave`: front at 1.35 km/s, red trailing 150 km behind it, glowing only above where density falls to 5e-9 of sea level (or the boundary) | `0.05 nits x min(sqrt(kt/3800), 1.5)`; eases in over 20 s, e-folds 240 s, gone at 600 s[^ksarmory-redwave] | `CentreRadius` = burst, `-nits`; `FireSun` = front radius, trail, 3, red floor altitude | `DrawRedWave`: chords through two spheres in closed form, four height samples per piece, no noise |

All four **add** light to the HDR scene (`imageStore(sceneColor, was + colour x nits x level)`, alpha
kept) and stop each ray at the scene depth, so terrain and craft occlude them. The magnetic field's
direction for the shell is `3(a.u)u - a` for the same spin-axis dipole (`DebrisShell.FieldDirection`),
and the bubble's field strength is `B0 (R/r)^3 sqrt(1 + 3 sin^2 lat)` from the body's `FieldTesla`.[^ksarmory-debrisbubble]

### 3. One shader, one branch, told apart by a negative bound

The sky shares the mushroom cloud's pipeline, `Push` struct and `BindCloud` call; nothing new is
bound.[^ksarmory-cloudpass] `SkyDispatch` names the contract:[^ksarmory-skydispatch]

- `CentreRadius.w < 0` marks a sky dispatch, and its magnitude is the brightness (a cloud's is a
  positive bounding radius).
- `FireSun.z` is the kind, tested from the highest down (`> 2.5` red wave, `> 1.5` debris,
  `> 0.5` aurora, else glow).
- `FireSun.w` belongs to the kind. The ground-mark branch offsets the invocation by `FireSun.xy`
  whenever `FireSun.w > 0`; it now also requires `CentreRadius.w >= 0`, because an aurora's
  `FireSun.w` (sine of a northern latitude) used to shift every northern curtain off the screen.
  `SkyDispatchShaderTests` regex-checks both the threshold order and that guard in the GLSL text.[^ksarmory-skydispatch-shader-tests]

The shader's planet is KSA's: `global.lighting.planetPosition` and `planetRadius` (Global.glsl
L41, L46), which `Program.UpdatePlanetShaderData` fills per viewport from
`camera.GetEffectiveNearbyCelestial()` (`NearbyCelestial ?? Program.FindNearbyCelestial`), and only
when that body is an `AtmosphericBody` (Program.cs L2980, L3004-L3014).[^ksa-global-glsl][^ksa-program][^ksa-camera][^ksa-atmosphericbody]
The mod therefore draws a sky object only when its body is the recording camera's
`NearbyCelestial` (`ReferenceEquals(over, camera.NearbyCelestial)`); a glow over another planet would
otherwise be drawn as a shell round this one.

### 4. The budget: the newest of each kind

`Record` gathers every sky object into `_sky`, then `KeepTheNewestOfEachKind` asks
`SkyDispatch.Choose` to keep, per kind, up to `MostOf(kind)` (aurora 4, red wave 2, glow and debris 3)
**newest first**, and `DrawSky` dispatches each kind over the full screen
(`ceil(W/8) x ceil(H/8)`) in its own profiler region (`KSArmory Cloud: glow`, `aurora`, `debris`,
`red wave`), with a compute hazard barrier before each one that follows an earlier dispatch.[^ksarmory-cloudpass][^ksarmory-skydispatch]
Newest rather than brightest because brightness crosses as one burst fades and the next arrives,
and a brightness ranking made the drawn curtain jump between them. The lists themselves are capped
too: four glows, four waves, four curtains (`MaxGlows`, `MaxCurtains`), oldest dropped.[^ksarmory-nuclearclouds]
`SkyWanted` and `SkyDrawn` expose the counts.

The author's prices at 1440x900 from orbit: glow 0.13 ms, debris 0.06-0.09 ms, aurora 27.9 ms
before the slab/hoisting rework and 2.5 ms after (NUCLEAR-EFFECT.md L850-L865).[^ksarmory-nuclear-effect]

### 5. No air: one-shot emitters instead (`BurstEjecta`)

On `!hasAir`, `NuclearClouds.Begin` calls `BurstEjecta.Begin(body, burstCcf, chargeKg,
KsaWorld.HeightAboveTerrain(body, burstEcl))` and keeps the returned `AirlessBurst.Extent` for the
watching camera.[^ksarmory-nuclearclouds][^ksarmory-burstejecta]

| Step | Detail |
|---|---|
| Gate | `Detonation.ParticlesEnabled`, which reads `GameSettings.Current.Graphics.Particles` (GameSettings.cs L361)[^ksa-gamesettings] |
| Anchor | `BubbleOrigin { Time = Universe.GetElapsedTime(), Parent = body, BubFrame = BubbleFrame.Ccf, PositionBub = burstCcf, VelocityBub = 0 }`: at rest in the body's rotating frame, the same form `Celestial.TrySpawnGroundImpact` builds (Celestial.cs L262)[^ksa-bubbleorigin][^ksa-bubbleframe][^ksa-universe][^ksa-celestial] |
| Shell | `KSArmoryNuclearShell`: one `Burst` particle, `ParticleInfo.Size` = `AirlessBurst.ShellRadius` (2.6 peak fireball radii), `Lifespan` = the vacuum flash's duration; it grows only through its XML `ScaleEnvelope`, because a velocity read as a blob flying off[^ksarmory-airlessburst][^ksarmory-particles-xml] |
| Ejecta | Only if `ThrowsEjecta`: the fireball radius exceeds the height above **terrain**. `KSArmoryNuclearEjecta` (128 particles, `Burst`, sphere spawn, `MoveAwayFromCenter`, `ParticleVelocityShift` 0.25): spawn radius 0.4 fireball radii, velocity `(v, v, v)` with `v = sqrt(reach x g)` for a reach of 4 fireball radii at 45 degrees, lifespans `v sqrt(2) / g x (1 -/+ 0.25)` so a grain's life matches the speed spread |
| Physics | None in the mod. KSA's `ParticleEmitter.ApplyAtmosphereResponse` treats ambient pressure under 100 Pa as no air (gravity scale 1, drag 0), and `GetAmbientAtmosphere` returns zero above `Height` or with no atmosphere, so the engine flies the grains on ballistic arcs under local gravity (ParticleEmitter.cs L471-L494)[^ksa-particleemitter] |
| Fire | `ParticleSystem.GetAndInitializeEmitters(id, out handles)`, then per emitter `LocalOffset = Identity`, `Context.Vehicle = null`, `Context.Part = null`, `Context.Astronomical = body`, `Origin`, tune, `body.AddEmitter(handle)`: the celestial branch of `ExplosionSystem.FireStage` (L479-L538)[^ksa-particlesystem][^ksa-explosionsystem] |
| Release | Nothing to do. A `Burst` emitter spawns once in `TrySpawnParticles` and marks spawning complete; `HasCompletedSimulation` turns true after its longest particle decays, `TryUnregisterEmitter` resets it after `MaxFramesInFlight` frames, and `Celestial` drops handles that are no longer valid (Celestial.cs L1540-L1546)[^ksa-emitterspawnmode][^ksa-particleemitter][^ksa-celestial] |

The same `Fire` plumbing serves `BeginWilson`, the condensation shell of a dense, moist-air burst
(`!thin && dryness < 0.5`), which sets `Opacity = 4 / (1.12 x radius)` so KSA's per-metre volumetric
density stays translucent at any size.[^ksarmory-burstejecta] The emitter pool rules themselves are
in [pooled-particle-emitters](./pooled-particle-emitters.md). An airless burst's ground mark uses
`AirlessBurst.ScorchRadius`, a radiation-fluence law (`sqrt` of the yield) instead of the blast law.[^ksarmory-airlessburst]

### 6. The rest of the thin-air rules in `Sim/`

| File | What it decides | Who uses it |
|---|---|---|
| `ThermalAltitude` | Glasstone and Dolan's thermal pulse timing (`PulseScale`), radiated share (`ThermalFraction`: 0.35 low, 0.6 in the stratosphere, 0.25, then 0.03), how long the ball glows (`GlowScale`, Orange to Teak) and the double pulse merging (`SinglePulse`)[^ksarmory-thermalaltitude] | `MushroomCloud.FlashSeconds` / `GrowthSeconds` / `WhiteHotSeconds` and `FlashGlare`, so the ball `DebrisShell.At` starts from already reflects the burst's air[^ksarmory-mushroomcloud] |
| `FireballBlackout` | Radar-only: a sphere of 1.5 peak fireball radii, lasting `60 s x cbrt(Mt)`, shrinking over its last 30%, independent of altitude[^ksarmory-fireballblackout] | `NuclearClouds.BlacksOut` centres it on the cap; `Radar` drops a contact behind it when the sensor emits and `Config.NuclearBlackout` is on (Radar.cs L254-L260)[^ksarmory-radar] |
| `BurstRegime` | `Dryness` (condensation off from 11 to 16 km on Earth, always on a body that does not condense); `StandsAsMushroom`, `XRaysEscape`, `Blasts`, body-aware versions of the thresholds above[^ksarmory-burstregime] | Only `Dryness` is called from `src/`; the other three are exercised by `BurstRegimeTests` (which pins them equal to the fixed thresholds on Earth) and nothing else[^ksarmory-burstregime-tests] |
| `Bodies.xml` / `BodyTraits` | Per body Id: `FieldTesla`, tilt, azimuth, `Airglow`, `Condensation`, `Gamma`, `Surface`, `XRayOpacity`; neutral defaults for unknown bodies[^ksarmory-bodytraits][^ksarmory-bodies-xml] | Read from every enabled mod's `KSArmory/*.xml` whose root is `<Bodies>` (`InstalledPacks.Read`), so a system mod can describe its own bodies[^ksarmory-installedpacks] |

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `startup:all-mods-loaded` | `[StarMapAllModsLoaded] OnFullyLoaded` -> `InstalledPacks.RegisterAll()` (KSArmoryMod.cs L158) | `Bodies.xml` files go into `BodyCatalogue`[^ksarmory-mod] |
| `frame:draw-ui` (or `frame:celestials` with the HUD hidden) | `StepSimulation` from `[StarMapAfterGui]` (Program.cs L2268) or the `OnFrameCelestials` prefix (L2320); `WeaponSystem.Detonate` calls `NuclearClouds.Begin` (WeaponSystem.cs L3962) | Regime chosen, sky lists filled, airless emitters fired and added to the body[^ksarmory-weaponsystem] |
| same step | `NuclearClouds.Update(_lastSimStep)` (KSArmoryMod.cs L934), or `Clear()` when `Config.NuclearClouds` is off | Every glow, wave and curtain ages by the **simulated** step and expires at its life[^ksarmory-config] |
| `frame:pre-render` | `Program.OnPreRender`: `ParticleSystem.UpdateEmitters(num)` (L2417) | The `Burst` emitters spawn their particles the same frame |
| `frame:render` | `Render` -> `UpdateShaderData` per viewport (L2464) -> `UpdatePlanetShaderData` | The planet block the sky shader reads |
| `render:particles` | `ParticleSystem.UpdateParticles` inside `RenderGame` (L4850) | Ejecta integrated on the GPU |
| `render:bloom` | Harmony prefix on `SunbloomRenderer.Render` (call at L4915) -> `CloudPass.Record` -> `DrawSky` x4 | After the fire light, ground marks, rings, holes and leak streams; before the cloud march, its resolve, the shock front, tracers and the whiteout[^ksa-sunbloom][^ksa-program] |

Why these points:

- The models live in static lists anchored in the body's rotating frame (`BurstCcf`, `FootCcf`) and
  are converted to ecliptic and then camera-relative in double precision every time the pass asks,
  so a curtain stays over the same ground as the planet turns.
- Ageing on the simulated step means a pause freezes the sky and time warp runs it faster, matching
  the cloud.
- Recording inside the bloom hook means the added light blooms and is tonemapped with the scene and
  is occluded by its depth. It shares every gate of the cloud: `Config.ShaderPass > 0`, a
  `NearbyCelestial`, and the weather shadow buffers found (`TryWeatherShadowBuffers`) for that body.
- The prefix runs per recorded viewport, and `Program.GetRenderCamera()` returns that viewport's
  camera (`RenderedViewport.GetCamera()`), so camera windows draw their own sky.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L848-L945) | `Begin`: air read, same-burst merge, which sky kinds start, airless hand-off |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L211-L383) | `TryWave`, `TryAurora`, `JoinTheSameBurst`, `Light` (curtains), `TryGlow` |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L663-L728) | `IsThin`, `TryDebris` (shell or bubble) |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L552-L652) | Gathers the four kinds into push constants and draws them |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L82-L129) | `KeepTheNewestOfEachKind`, `DrawSky` |
| [src/KSArmory/Sim/SkyDispatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SkyDispatch.cs) | Kind ids, `FireSun.w` contract, per-kind cap, newest-first choice |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1205-L1682) | `DrawAurora`, `DrawDebris`, `DrawRedWave`, `DrawXRayGlow` |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1684-L1729) | Mark-offset guard and the sky branch in `main` |
| [src/KSArmory/Sim/XRayGlow.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/XRayGlow.cs) | Layer height, thickness, strength and green share |
| [src/KSArmory/Sim/Aurora.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Aurora.cs) | Dipole field-line footpoints, magnetic east, strength |
| [src/KSArmory/Sim/RedWave.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RedWave.cs) | Front speed, trail, strength, red floor |
| [src/KSArmory/Sim/DebrisShell.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DebrisShell.cs) | Thin-air debris shell: glow, colour, fill, stretch along the field |
| [src/KSArmory/Sim/DebrisBubble.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DebrisBubble.cs) | Field-held bubble above the air; field strength and share |
| [src/KSArmory/Sim/BodyAir.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BodyAir.cs) | Column above a height, altitude of a column, boundary |
| [src/KSArmory/Ksa/BurstEjecta.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstEjecta.cs) | Airless shell and ejecta emitters, Wilson cloud, `Fire` |
| [src/KSArmory/Sim/AirlessBurst.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/AirlessBurst.cs) | Shell size, ejecta throw, scorch reach, camera extent |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L347-L520) | `KSArmoryNuclearEjecta`, `KSArmoryNuclearWilson`, `KSArmoryNuclearShell` |
| [src/KSArmory/KSArmory/Bodies.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory/Bodies.xml) | Field, airglow, condensation, gamma, X-ray opacity per body |
| [tests/KSArmory.Tests/SkyDispatchShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/SkyDispatchShaderTests.cs) | Shader-text test of the kind thresholds and the offset guard |

## Snippets

```csharp
// src/KSArmory/Ksa/NuclearClouds.cs L900-L918
            double layer = XRayGlow.LayerAltitude(KsaWorld.BodyAirOf(body));
            double overLayer = Vec.Len(burstCcf) - body.MeanRadius - layer;
            if (hasAir && Aurora.Lights(airRatio)) Light(body, burstCcf, chargeKg);

            if (hasAir && RedWave.Lights(airRatio))
            {
                if (_waves.Count >= MaxGlows) _waves.RemoveAt(0);
                _waves.Add(new Glow { Body = body, BurstCcf = burstCcf, ChargeKg = chargeKg });
            }

            if (hasAir && XRayGlow.Lights(airRatio)
                && XRayGlow.Strength(kt, overLayer, XRayGlow.RiseSeconds) > XRayGlow.FaintestNits)
            {
                if (_glows.Count >= MaxGlows) _glows.RemoveAt(0);
                _glows.Add(new Glow { Body = body, BurstCcf = burstCcf, ChargeKg = chargeKg });
                Log.Info($"nuclear burst over the air: its X-rays light the layer {layer / 1000.0:F0} km "
                         + $"up, {XRayGlow.Strength(kt, overLayer, 2.0):F2} "
                         + "nits under it at first, red for minutes");
            }
```

```csharp
// src/KSArmory/Ksa/CloudPass.cs L583-L602
            for (int i = 0; i < NuclearClouds.AuroraCount; i++)
            {
                if (!NuclearClouds.TryAurora(i, out double3 footEcl, out double3 eastEcl, out double sinLatitude,
                                             out double nits, out double age, out object? over)) continue;
                if (!ReferenceEquals(over, camera.NearbyCelestial)) continue;

                double3 foot = footEcl - camera.PositionEcl;
                if (!Vec.IsFinite(foot)) continue;

                _sky.Add((SkyDispatch.Aurora, nits, new Push
                {
                    InvViewProj = camera.VPInv.viewProjection,
                    CentreRadius = new float4((float)foot.X, (float)foot.Y, (float)foot.Z, -(float)nits),
                    FireSun = new float4((float)Aurora.BottomAltitude(KsaWorld.BodyAirOf(camera.NearbyCelestial)),
                                         (float)Aurora.TopAltitude(KsaWorld.BodyAirOf(camera.NearbyCelestial)),
                                         SkyDispatch.Aurora,
                                         (float)sinLatitude),
                    Shape = new float4((float)eastEcl.X, (float)eastEcl.Y, (float)eastEcl.Z, (float)age),
                }));
            }
```

```glsl
// src/KSArmory/Shaders/KSArmoryCloud.comp L1696-L1729
    // Only for a dispatch that is the ground alone: on a cloud's those two floats are the
    // fireball's radius and glow, on a flash-only one there is nothing to offset, and on a sky
    // dispatch (a negative bound) FireSun.w is the dispatch's own -- an aurora's positive for a
    // northern foot, which offset here drew every northern curtain off the screen.
    if (pc.FireSun.w > 0.0 && pc.CentreRadius.w >= 0.0
        && !(pc.AgeStrengthWind.y > 0.0 && pc.CentreRadius.w > 0.0))
    {
        at += ivec2(pc.FireSun.xy);
    }
    // ...
    if (at.x >= size.x || at.y >= size.y) return;

    // A LAYER A HIGH BURST LIT, on a dispatch of its own that the negative bound marks.
    if (pc.CentreRadius.w < 0.0)
    {
        if (pc.FireSun.z > 2.5) DrawRedWave(at, size);
        else if (pc.FireSun.z > 1.5) DrawDebris(at, size);
        else if (pc.FireSun.z > 0.5) DrawAurora(at, size);
        else DrawXRayGlow(at, size);
        return;
    }
```

```csharp
// src/KSArmory/Ksa/BurstEjecta.cs L242-L269
    private static void Fire(string id, Celestial body, BubbleOrigin origin,
                             Action<ParticleEmitter<ParticleUpdateData, ParticleRenderData>> tune)
    {
        if (!Program.Instance.ParticleSystem.GetAndInitializeEmitters(id, out var handles)
            || handles is null || handles.Count == 0)
        {
            if (!_warned)
            {
                _warned = true;
                Log.Warn($"no free emitters for '{id}'; the burst will draw without it");
            }
            return;
        }

        // The order KSA's own ExplosionSystem.FireStage uses: point it, tune it, then register it.
        foreach (var handle in handles)
        {
            if (handle.TryGet() is not { } emitter) continue;

            emitter.LocalOffset = float4x4.Identity;
            emitter.Context.Vehicle = null;
            emitter.Context.Part = null;
            emitter.Context.Astronomical = body;
            emitter.Origin = origin;
            tune(emitter);
            body.AddEmitter(handle);
        }
    }
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` | [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326) | Prefix target the sky is recorded in (via the shared pass) |
| `Program.UpdatePlanetShaderData`, `UpdateShaderData`, `GetRenderCamera`, `OnPreRender` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3004-L3014) | Planet block for the shader (AtmosphericBody only); recorded viewport's camera (L641); emitter spawn at L2417 |
| `Camera.GetEffectiveNearbyCelestial`, `NearbyCelestial` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L164-L167) | Which planet the global block describes |
| `global.lighting.planetPosition`, `planetRadius` | [current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L41-L46) | Planet the layers, curtains and red floor are measured from |
| `Astronomical.GetAtmosphereReference`, `GetOceanReference` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L387-L392) | Whether a body has air; its physical atmosphere |
| `PhysicalAtmosphereReference.Height`, `ScaleHeight`, `GetAtmosphericDensityAtAltitude`, `GetAtmosphericPressureAtAltitude`, `CalculateBoundaryHeight` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L43-L88) | Isothermal air the ratios and columns come from |
| `Celestial.GetRotationAxisCce`, `GetCce2Ccf`, `MeanRadius`, `AddEmitter`, `TrySpawnGroundImpact` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L252-L262) | Dipole axis (L615), rotating frame (L538), emitter registration, the engine's own Ccf-anchored effect |
| `ParticleSystem.GetAndInitializeEmitters` | [current/decomp/KSA.Rendering.Particles/ParticleSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleSystem.cs#L259-L276) | Takes the shell, ejecta and Wilson emitters from the pool by Id |
| `ParticleEmitter` `EmitterSpawnInfo`, `ParticleInfo`, `Context`, `LocalOffset`, `Opacity`, `Origin`, `Handle.TryGet`, `TrySpawnParticles`, `HasCompletedSimulation`, `ApplyAtmosphereResponse` | [current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ParticleEmitter.cs#L471-L538) | Tuning, one-shot spawn, self-release, vacuum ballistics |
| `EmitterSpawnMode.Burst` | [current/decomp/KSA.Rendering.Particles/EmitterSpawnMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/EmitterSpawnMode.cs) | Spawn once, then complete |
| `ExplosionSystem.FireStage` | [current/decomp/KSA/ExplosionSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSystem.cs#L479-L538) | The order the mod copies for a celestial-anchored emitter |
| `BubbleOrigin`, `BubbleFrame.Ccf`, `Universe.GetElapsedTime` | [current/decomp/KSA/BubbleOrigin.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleOrigin.cs#L9-L29) | The emitter's anchor at rest on the turning body ([current/decomp/KSA/BubbleFrame.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleFrame.cs), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2399) L2399) |
| `GameSettings.Graphics.Particles` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L361) | Particles off means no airless effect |
| `AtmosphericBody` | [current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs#L5) | Only these get a planet block, so only these show a sky |
| Earth's `<Physical>` atmosphere | [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L1336-L1339) | 1 atm, 1.225 kg/m3, 8 km: the calibration body |

## Pitfalls and version notes

- **The regime thresholds are Earth's air ratios.** Which kinds start is decided by fixed ratios
  (`0.01`, `2.5e-4`, `1e-4`, `1e-5`); only the heights they are drawn at move with the body's column.
  `BurstRegime.StandsAsMushroom` and `XRaysEscape`, the body-aware versions, are not called from
  `src/` (NUCLEAR-ALTITUDE.md step 11: "nothing calls them yet").[^ksarmory-nuclear-altitude]
- **Several `Bodies.xml` traits are parsed and never read.** No code consults `HasField`,
  `FieldTiltDeg`, `FieldAzimuthDeg` or `Airglow`: `NuclearClouds.Light` starts curtains on any body
  with air whatever its field (a body with no `FieldTesla` still gets curtains), the dipole is always on
  the spin axis, and the shader's colours are fixed oxygen and nitrogen lines. Only `FieldTesla`
  (the bubble), `XRayOpacity` (the layer height) and `Condensation` (dryness) reach the sky. The
  `BodyTraits` summary and NUCLEAR-ALTITUDE.md L271-L272 promise "no field: no aurora", and the doc
  calls the attribute `MagneticField`; the reader takes `FieldTesla`.[^ksarmory-bodytraits][^ksarmory-bodies-xml]
- **A failed air read is sea level.** `AirDensityRatioAt` returns 1.0 from its `catch`, so a high
  burst over a body whose atmosphere throws gets a dense-air mushroom and no sky.[^ksarmory-ksaworld]
- **Only the camera's body.** A burst over another planet draws nothing until the camera's
  `NearbyCelestial` is that planet, and a body that is not an `AtmosphericBody` never has the planet
  block filled.
- **Twelve full-screen dispatches at worst.** The per-kind cap allows 4 + 3 + 3 + 2; the pass does
  not shed sky work when `CloudPassCost` reads over budget (NUCLEAR-ALTITUDE.md L231).
- **Shared gates.** `Config.ShaderPass` at 0, a failed weather-shadow lookup, or `Config.NuclearClouds`
  off removes the sky with the cloud; only the red wave has its own switch (`Config.RedWave`).
- **One warning for every emitter failure.** `BurstEjecta._warned` is shared by `Begin`,
  `BeginWilson` and `Fire`, so after the first pool shortage or exception the rest are silent.
- **Doc drift.** NUCLEAR-EFFECT.md L865 and NUCLEAR-ALTITUDE.md L231-L232 still say "at most four
  are drawn, each kind's brightest"; the code keeps the newest of each kind (the change is described
  at NUCLEAR-ALTITUDE.md L113-L117). The "Wrong" list at NUCLEAR-ALTITUDE.md L31-L59 is the
  pre-plan audit: the airless `AirRatio = 1.0` it cites is now `0.0` (NuclearClouds.cs L927) and
  the stacked bus sky is merged by `JoinTheSameBurst`.[^ksarmory-nuclear-altitude]
- **Not modelled:** EMP, radiation belts and altitude-scaled radio blackout are listed as not built
  (NUCLEAR-NEXT.md L480-L520).[^ksarmory-nuclear-next]

## See also

- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Core shader library include](./core-shader-library-include.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Burst flash and view shake](./burst-flash-and-view-shake.md)
- [Shock-front refraction](./shock-front-refraction.md)
- [Fireball light injection](./fireball-light-injection.md)
- [Burst sound at blast arrival](./burst-sound-at-blast-arrival.md)
- [Pooled particle emitters](./pooled-particle-emitters.md)
- [Chase camera on a round](./chase-camera-on-a-round.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Run several effects through one compute pipeline: a kind tag and a per-kind dispatch cap](../../../howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-2.md) - how-to article demonstrating this feature
- [Draw analytic sky effects in a compute pass: X-ray glow, curtains and debris shells chosen by the air](../../../howto/shaders/compute-effects/raymarch-a-local-volume-into-the-hdr-scene/raymarch-a-local-volume-into-the-hdr-scene-3.md) - how-to article demonstrating this feature

[^ksarmory-nuclearclouds]: src/KSArmory/Ksa/NuclearClouds.cs
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-cloud-comp]: src/KSArmory/Shaders/KSArmoryCloud.comp
[^ksarmory-skydispatch]: src/KSArmory/Sim/SkyDispatch.cs
[^ksarmory-xrayglow]: src/KSArmory/Sim/XRayGlow.cs
[^ksarmory-aurora]: src/KSArmory/Sim/Aurora.cs
[^ksarmory-redwave]: src/KSArmory/Sim/RedWave.cs
[^ksarmory-debrisshell]: src/KSArmory/Sim/DebrisShell.cs
[^ksarmory-debrisbubble]: src/KSArmory/Sim/DebrisBubble.cs
[^ksarmory-burstregime]: src/KSArmory/Sim/BurstRegime.cs
[^ksarmory-thermalaltitude]: src/KSArmory/Sim/ThermalAltitude.cs
[^ksarmory-fireballblackout]: src/KSArmory/Sim/FireballBlackout.cs
[^ksarmory-airlessburst]: src/KSArmory/Sim/AirlessBurst.cs
[^ksarmory-burstejecta]: src/KSArmory/Ksa/BurstEjecta.cs
[^ksarmory-mushroomcloud]: src/KSArmory/Sim/MushroomCloud.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-bodyair]: src/KSArmory/Sim/BodyAir.cs
[^ksarmory-bodytraits]: src/KSArmory/Sim/BodyTraits.cs
[^ksarmory-bodies-xml]: src/KSArmory/KSArmory/Bodies.xml
[^ksarmory-installedpacks]: src/KSArmory/Ksa/InstalledPacks.cs
[^ksarmory-particles-xml]: src/KSArmory/KSArmoryParticles.xml
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-radar]: src/KSArmory/Ksa/Radar.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-skydispatch-shader-tests]: tests/KSArmory.Tests/SkyDispatchShaderTests.cs
[^ksarmory-burstregime-tests]: tests/KSArmory.Tests/BurstRegimeTests.cs
[^ksarmory-nuclear-altitude]: docs/NUCLEAR-ALTITUDE.md
[^ksarmory-nuclear-effect]: docs/NUCLEAR-EFFECT.md
[^ksarmory-nuclear-next]: docs/NUCLEAR-NEXT.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-sunbloom]: KSA.SunbloomRenderer (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmosphere]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-bubbleorigin]: KSA.BubbleOrigin (decompiled, build 2026.10.10.5554)
[^ksa-bubbleframe]: KSA.BubbleFrame (decompiled, build 2026.10.10.5554)
[^ksa-explosionsystem]: KSA.ExplosionSystem (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-particlesystem]: KSA.Rendering.Particles.ParticleSystem (decompiled, build 2026.10.10.5554)
[^ksa-particleemitter]: KSA.Rendering.Particles.ParticleEmitter (decompiled, build 2026.10.10.5554)
[^ksa-emitterspawnmode]: KSA.Rendering.Particles.EmitterSpawnMode (decompiled, build 2026.10.10.5554)
[^ksa-global-glsl]: Core/Shaders/Common/Global.glsl (game shader, build 2026.10.10.5554)
[^ksa-astronomicals-xml]: Core/Astronomicals.xml (game data, build 2026.10.10.5554)
