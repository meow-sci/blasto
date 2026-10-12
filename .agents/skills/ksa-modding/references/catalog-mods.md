# Community mods

Analyzed community KSA mods. Open a mod overview for its architecture and feature list, then a feature page for pinned code references.

## [AdvancedFlightComputer](mods/ksa-advancedflightcomputer/overview.md)

KSA-AdvancedFlightComputer · MIT · Maximilian Nesslauer and Oliver Cairns (cairn5) · starmap

Maneuver quick-tools, flyby targeting and multi-pass burns in the stock Transfer Planner, RCS-only burns, automatic staging and burn removal, hyperbolic targets, and closed-loop powered guidance for ascent, boostback and landing with native convex solvers

Features: [60 features](mods/ksa-advancedflightcomputer/features/index.md)

## [Compendium](mods/compendium/overview.md)

Compendium · MIT · nanonestor · starmap

Celestial encyclopedia window that joins a cross-mod JSON catalog with the live star-system tree and, through Harmony patches on the navball, gauges and flight computer, lets fixed stars and barycenters act as navigation-only targets

Features: [17 features](mods/compendium/features/index.md)

## [gatOS](mods/gatos/overview.md)

gatOS · MIT · meow sci · starmap

A KSA mod that boots a real Alpine Linux in a QEMU microVM beside the game, exposes live telemetry and controls as a 9P /sim filesystem plus HTTP, MQTT, MCP and serial APIs, and adds programmable camera, paint, decal, audio and physics features on top

Features: [45 features](mods/gatos/features/index.md)

## [Kitten Engineer Redux](mods/kitten-engineer-redux/overview.md)

Kitten-Engineer-Redux · MIT · BarneyTheGod · starmap

Small StarMap mod in the spirit of Kerbal Engineer Redux that adds a staging and mass analysis panel to the vehicle editor and a configurable flight HUD with orbit, suicide-burn, planner, alert and telemetry-graph sections, all drawn from Harmony postfixes on the game's own UI methods

Features: [14 features](mods/kitten-engineer-redux/features/index.md)

## [KittenExtensions Continued](mods/kittenextensions/overview.md)

KittenExtensions · MIT · tom_is_unlucky, AMPW · starmap

Foundational StarMap library mod that runs XPath patches over one combined XML document built from every installed mod's data files and lets other mods register new XML asset types, by redirecting the game's XmlLoader calls and extending its XmlSerializer overrides

Features: [10 features](mods/kittenextensions/features/index.md)

## [KSA Broadcast Telemetry Overlay](mods/ksa-public-telemetry/overview.md)

KSA-Public-Telemetry · MIT · elevatorctln · starmap

StarMap mod that draws a SpaceX-webcast-style telemetry bar over KSA flight with live gauges, an engine diagram built from real nozzle positions, a mission clock, inferred flight events and planned burns, while hiding and restoring the stock HUD

Features: [18 features](mods/ksa-public-telemetry/features/index.md)

## [KSA_AircraftHUD](mods/ksa-aircrafthud/overview.md)

KSA_AircraftHUD · MIT · UriahGnu · starmap

Fighter-style head-up display drawn with an ImGui draw list in a fullscreen window, styled by a ShaderExtensions ImGui window shader and backed by a canopy-glass post-process chain, with a custom font, an INI settings file and a ModMenu entry

Features: [11 features](mods/ksa-aircrafthud/features/index.md)

## [KSA-DeltaVMap (DeltaVMap)](mods/ksa-deltavmap/overview.md)

KSA-DeltaVMap · MIT · Maximilian-Nesslauer · starmap

An auto-generated, interactive delta-v subway map and transfer-window planner that reads every star system of the loaded universe live, prices routes with closed-form patched conics, and adds an opt-in transfer-window overlay to the game's 3D map mode

Features: [20 features](mods/ksa-deltavmap/features/index.md)

## [KSA-MeasureTools (MeasureTools)](mods/ksa-measuretools/overview.md)

KSA-MeasureTools · MIT · Maximilian-Nesslauer · starmap

Click-to-measure ruler, protractor, surface, circle and face-angle tools for the map view, the flight view and the vehicle editor, with snapping to bodies, disc edges, orbit lines and part geometry down to mesh vertices and feature edges

Features: [17 features](mods/ksa-measuretools/features/index.md)

## [KSACars (Fast & Purrious)](mods/ksacars/overview.md)

KSACars · MIT · LaurensDeV · starmap

Driveable cars for KSA, a beach buggy and a flying Cadillac Eldorado, built as single parts whose springs, tyres, engine, rockets, lights, sounds and driver animation are simulated by a StarMap code mod and injected into the engine's vehicle step

Features: [34 features](mods/ksacars/features/index.md)

## [KSAero](mods/ksaero/overview.md)

KSAero · MIT · cairn5 · starmap

Small StarMap mod that replaces stock atmospheric drag with a body-of-revolution lift and drag model by rewriting one call site in the vehicle force function

Features: [9 features](mods/ksaero/features/index.md)

## [KSAGolf](mods/ksagolf/overview.md)

KSAGolf · MIT · LaurensDeV · starmap

A golf mod foundation for Kitten Space Agency with a working putting game and a minigolf course builder on top of extensive frame-timing, camera, kitten-posing, terrain and developer tooling

Features: [33 features](mods/ksagolf/features/index.md)

## [KSArmory](mods/ksarmory/overview.md)

KSArmory · MIT · LaurensDeV · starmap

Weapon systems for Kitten Space Agency built as reusable machinery (sensors, fire control, sub-frame projectiles, articulated mounts, a ballistic computer, compute-shader effects) with each weapon a registry entry plus art, bound to the game by six StarMap hooks and six Harmony patches

Features: [80 features](mods/ksarmory/features/index.md)

## [KSAStructures](mods/ksastructures/overview.md)

KSAStructures · MIT · LaurensDeV · starmap

A StarMap code mod that stands a walkable office tower on Earth as launch-pad static objects dug into the terrain at load, with a lift, an underground garage lit by shadow-casting lamps, a spiral ramp, a chair and computer that fly the nearest craft, and a first-person kitten view

Features: [25 features](mods/ksastructures/features/index.md)

## [Lonely Planets](mods/lonelyplanets/overview.md)

LonelyPlanets · CC-BY-NC-SA-4.0 · Jared Melchers · native

Native XML systems select one planet around Sol with a custom starter vessel and situation

Features: [3 features](mods/lonelyplanets/features/index.md)

## [MPFX](mods/mpfx/overview.md)

MPFX · MIT · AMPW · starmap

Small post-processing mod by AMPW that declares a 32-pass color-grading chain as ShaderExtensions XML assets, feeds it from an ImGui settings window each frame and keeps profiles only as hand-copied XML

Features: [10 features](mods/mpfx/features/index.md)

## [Oh My Stars](mods/ohmystars/overview.md)

OhMyStars · GPL-2.0 · nanonestor · starmap

StarMap mod that draws constellation lines, names, IAU boundaries and an RA/Dec grid on the sky from Stellarium and AT-HYG data, with a star browser, navball marker, orient-to-star mode and a star field editor

Features: [16 features](mods/ohmystars/features/index.md)

## [purrTTY](mods/purrtty/overview.md)

purrtty · MIT · purrTTY Project (meow sci) · starmap

In-game terminal emulator for KSA that hosts real shells (ConPTY, POSIX pty, in-game console, other mods' shells) on libghostty-vt and renders them as ImGui windows or as textures on world-space quads

Features: [35 features](mods/purrtty/features/index.md)

## [Real Atmospheres](mods/realatmospheres/overview.md)

RealAtmospheres · MIT · Brimfyr · starmap

StarMap mod that rebuilds the Sol system's atmospheres, hazes and clouds from measured data by loading patched copies of KSA's shaders and system XML at launch, and swaps in measured flight atmosphere profiles

Features: [21 features](mods/realatmospheres/features/index.md)

## [ShaderExtensions](mods/shaderextensions/overview.md)

ShaderExtensions · MIT · AMPW · starmap

Shader infrastructure mod adding XML-declared fragment shaders with extra texture, uniform-buffer and push-constant bindings, per-window ImGui shaders, and RenderPassId-ordered post-processing chains before and after the ImGui pass via a Program.RenderGame transpiler

Features: [16 features](mods/shaderextensions/features/index.md)

## [StarMap](mods/starmap/overview.md)

StarMap · MIT · KlaasWhite · loader

The community code-mod loader for KSA, which launches the game in its own process, loads each mod's DLL into an isolated AssemblyLoadContext, resolves mod dependencies, and calls mods back at seven lifecycle points through Harmony patches

Features: [13 features](mods/starmap/features/index.md)

## [unscience](mods/unscience/overview.md)

unscience · MIT · meow sci · starmap

A meow-sci monorepo supermod that bundles 31 feature sub-mods (welding, scaling, decals, plumes, rings, cameras, kittens, saves and more) behind one StarMap entry class, one Harmony instance and one ImGui toolbox

Features: [106 features](mods/unscience/features/index.md) · Submods: [34 submods](mods/unscience/submods/index.md)
