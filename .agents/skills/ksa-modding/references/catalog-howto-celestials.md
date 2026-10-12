# Celestials

Celestial data and surface placement: star systems and the body tree, body data and caches, atmosphere, cloud and ring data, star catalogs and star binaries, terrain queries and modifiers, latitude and longitude picks, static objects on a planet surface and ground clutter.

## Terrain

The surface: terrain height and line of sight, slope and ground contact, terrain decals, static objects, ground clutter and latitude and longitude picks.

### [Add terrain decals from code and flatten or dig the ground under a custom site](howto/celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md)

Add DecalModifierReference terrain modifiers to a body at load so the ground is drawn, collided with, queried and cluttered consistently, and compose a levelling decal with a height-map pit that meets the stock launch pad

- [Deserialize DecalModifierReference XML and add it to both modifier lists before the renderer is built](howto/celestials/terrain/add-terrain-decals-and-dig-a-site/add-terrain-decals-and-dig-a-site-1.md) — advanced
- [Levelling decal plus additive pit height map, ordered after the stock pad decal](howto/celestials/terrain/add-terrain-decals-and-dig-a-site/add-terrain-decals-and-dig-a-site-2.md) — advanced

### [Convert a click on a planet to latitude and longitude, measure between points and draw the great-circle arc](howto/celestials/terrain/click-a-planet-to-get-latitude-and-longitude/overview.md)

Ray-test the cursor against each body's mean sphere, convert the hit to latitude and longitude through the body-fixed frame, measure great-circle distance and bearing, and draw the arc only on the visible hemisphere

- [Sphere pick, GetLatitudeFromCce and GetLongitudeFromCce, haversine, chord and bearing](howto/celestials/terrain/click-a-planet-to-get-latitude-and-longitude/click-a-planet-to-get-latitude-and-longitude-1.md) — intermediate
- [Draw a great-circle arc over a planet without drawing through it: 48 Rodrigues samples with a back-face test](howto/celestials/terrain/click-a-planet-to-get-latitude-and-longitude/click-a-planet-to-get-latitude-and-longitude-2.md) — intermediate

### [Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](howto/celestials/terrain/exclude-or-replace-ground-clutter/overview.md)

Refill the four launch-pad exclusion zones the engine bakes at load, swap a body's clutter types for imported models at the solver handoff, and carry knocked-out or displaced instances across swaps and saves

- [Harmony postfix on the private PopulateLaunchPadExclusionZones with the celestial injected](howto/celestials/terrain/exclude-or-replace-ground-clutter/exclude-or-replace-ground-clutter-1.md) — intermediate
- [Prefix on ExecuteNextClothSolvers builds private clutter bundles with originals kept for exact restore](howto/celestials/terrain/exclude-or-replace-ground-clutter/exclude-or-replace-ground-clutter-2.md) — advanced
- [Per-grid memory with AND-merge, public-API snapshot and replay, native plus sidecar saves](howto/celestials/terrain/exclude-or-replace-ground-clutter/exclude-or-replace-ground-clutter-3.md) — advanced

### [Measure ground slope and surface normal, and detect when a mod-simulated point hits the ground](howto/celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/overview.md)

Derive the local gradient or surface normal from neighbouring height samples and test ground contact for bodies the engine does not simulate, with interpolation and epoch corrections

- [Gradient from centred east, west, north and south differences](howto/celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/measure-slope-and-detect-ground-contact-on-terrain-1.md) — intermediate
- [Surface normal from four one-metre samples and a cross product](howto/celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/measure-slope-and-detect-ground-contact-on-terrain-2.md) — beginner
- [Per-frame centre and radius, crossing test, interpolation and near-surface resample](howto/celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/measure-slope-and-detect-ground-contact-on-terrain-3.md) — advanced

### [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](howto/celestials/terrain/place-static-objects-on-a-planet-surface/overview.md)

Stand XML static objects on a planet with landmark references, redraw them when the engine culls them, and anchor mod-drawn models in the body-fixed frame so they follow rotation and restore from saves

- [IsLaunchPad landmarks added at StarMapAllModsLoaded and a render-data postfix for culled objects](howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-1.md) — advanced
- [GroundAnchor composed each frame into a camera-relative matrix](howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-2.md) — advanced
- [Course frame anchored at the kitten and saved as latitude, longitude, heading and floor height](howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-3.md) — advanced

### [Query the terrain height field and test line of sight across terrain](howto/celestials/terrain/query-terrain-height-and-line-of-sight/overview.md)

Ask KSA how high the ground is along a direction from a body centre, choose between the Ccf and Cce entry points and between fast and accurate sampling, and build sea clamps, ground-contact tests, hover height and line of sight on that one call

- [Ccf versus Cce entry points, sea clamp, no-claim results and a fixed-budget line-of-sight test](howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-1.md) — intermediate
- [Answer as a body centre and surface radius, pick the body by depth, mask radar by horizon](howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-2.md) — advanced
- [Cache decorator that reuses a height sample until near the ground or moved](howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-3.md) — intermediate
- [Height of the terrain directly below a craft for hover and touchdown](howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-4.md) — beginner

## Atmospheres and rings

How bodies look: atmosphere and cloud XML, measured atmosphere tables, planetary rings, renderer rebuilds after data changes and the detailed-body choice.

### [Add planetary rings to a body or restyle existing rings at runtime](howto/celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/overview.md)

Build a PlanetaryRingsReference tree in code and assign it to a body template, or mutate an existing ring's meshes, textures and field settings, then dispose and recreate the rings renderer so the game rebuilds its baked ring data

- [Build the ring reference tree, assign it to BodyTemplate.RingsReference, dispose and recreate the rings renderer](howto/celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-1.md) — advanced
- [Mutate the public ring reference tree, snapshot originals and recreate the rings renderer](howto/celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-2.md) — advanced

### [Change loaded game data at runtime and make the renderer rebuild from it](howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md)

Many KSA renderers bake XML-backed reference data into GPU buffers in their constructors, so editing the reference object does nothing until the renderer is recreated; four patterns for mutate-and-recreate, transactional swap, template assignment and in-place buffer writes

- [Snapshot, mutate the public tree and recreate one renderer by reflection](howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/change-loaded-game-data-and-make-the-renderer-notice-1.md) — advanced
- [Transactional apply and exact restore of a body's clutter data](howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/change-loaded-game-data-and-make-the-renderer-notice-2.md) — advanced
- [Assign to the template and call Program.RebuildRenderer](howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/change-loaded-game-data-and-make-the-renderer-notice-3.md) — advanced
- [Reflect the mapped buffer and slot, clamp when the camera is near, restore on leaving](howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/change-loaded-game-data-and-make-the-renderer-notice-4.md) — intermediate

### [Control which body the game picks as the camera's nearby celestial](howto/celestials/atmospheres-and-rings/choose-which-body-the-game-renders-in-detail/overview.md)

The game draws one body per camera in full detail and every other as a distant sphere, chosen by CelestialSystem.GetNearbyAstronomical; a small moon can steal the slot and make its planet flicker, fixed here by a guarded transpiler

- [Transpile the distance comparison into a ranking call that favours a body within 25 radii](howto/celestials/atmospheres-and-rings/choose-which-body-the-game-renders-in-detail/choose-which-body-the-game-renders-in-detail.md) — advanced

### [Define atmospheres, scattering and cloud layers for a body in system XML](howto/celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md)

Promote a body to AtmosphericBody with a complete Atmosphere block, tune Rayleigh, Mie and ozone terms, add a sibling Clouds block with coverage masks, detail tiles, flowmaps and cloud types, and build the textures offline

- [Promote a body to AtmosphericBody and splice generated Atmosphere values into the XML](howto/celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/define-atmospheres-and-cloud-layers-in-system-xml-1.md) — advanced
- [Templated decks and laminae with absolute asset paths](howto/celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/define-atmospheres-and-cloud-layers-in-system-xml-2.md) — advanced
- [Python asset scripts with encodings matched to the shader that reads them](howto/celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/define-atmospheres-and-cloud-layers-in-system-xml-3.md) — intermediate

### [Generate measured atmosphere tables from reference profiles](howto/celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/overview.md)

Build compact altitude tables offline from probe and reference-atmosphere data, apply KSA's own boundary rule, decimate to the nodes that reproduce ln density within tolerance, and emit one generated source file for flight and shaders

- [Python generator emitting checked-in C# tables for flight and shaders](howto/celestials/atmospheres-and-rings/generate-measured-atmosphere-tables-offline/generate-measured-atmosphere-tables-offline.md) — advanced

## Systems and stars

The loaded universe: composing systems and starting situations, enumerating systems and bodies, reading and caching body data, moving bodies, and the star catalog and star binaries.

### [Bundle a star catalog and constellation data and join them to the game's sky](howto/celestials/systems-and-stars/bundle-and-query-a-star-catalog/overview.md)

Ship a reduced AT-HYG CSV keyed by Hipparcos number, convert equatorial J2000 to the game's ecliptic directions, and optionally load Stellarium constellation files filtered to the stars present in the stock star binary

- [Header-indexed CSV parse into HIP-keyed ecliptic directions with synthetic keys](howto/celestials/systems-and-stars/bundle-and-query-a-star-catalog/bundle-and-query-a-star-catalog-1.md) — intermediate
- [Parse .fab and names files and filter to the stars present in the stock binary](howto/celestials/systems-and-stars/bundle-and-query-a-star-catalog/bundle-and-query-a-star-catalog-2.md) — intermediate

### [Cache and key per-body data so it survives system reloads, repeated ids and extra star systems](howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md)

Derived body data is constant for a loaded system but a cache must die when the system is reloaded or a body object is replaced; five invalidation and key designs from reference latches to prefix-keyed JSON catalogs

- [Latch on the Universe.CurrentSystem reference, not its Id](howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-1.md) — beginner
- [ConditionalWeakTable of immutable statics per Celestial](howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-2.md) — intermediate
- [One-shot attach on the first UI frame with a universe](howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-3.md) — beginner
- [Dotted path of Celestial ids from the top-level body down](howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-4.md) — intermediate
- [Prefix-keyed JSON files discovered in every installed mod folder](howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-5.md) — intermediate

### [Compose a reduced star system from library bodies](howto/celestials/systems-and-stars/compose-reduced-star-systems/overview.md)

Register a native system XML that selects a star and chosen library bodies, assigns their parents and home body, and optionally adds a local body asset

- [Native system XML with library references and an optional local body](howto/celestials/systems-and-stars/compose-reduced-star-systems/compose-reduced-star-systems.md) — intermediate

### [Enumerate every star system, star, planet and moon of the loaded universe](howto/celestials/systems-and-stars/enumerate-star-systems-and-bodies/overview.md)

Walk the loaded universe's roots and body tree, a flat CelestialSystem.All scan or a player-selected root, and avoid the type traps that stars are not Celestials and Children also lists vehicles

- [Roots then IParentBody.Children into an immutable forest, with role rules in a game-type-free class](howto/celestials/systems-and-stars/enumerate-star-systems-and-bodies/enumerate-star-systems-and-bodies-1.md) — intermediate
- [Universe.Roots, RootOf and a selected root, collecting only Celestials or only stars](howto/celestials/systems-and-stars/enumerate-star-systems-and-bodies/enumerate-star-systems-and-bodies-2.md) — beginner
- [Flat scan of CurrentSystem.All plus an explicit WorldSun](howto/celestials/systems-and-stars/enumerate-star-systems-and-bodies/enumerate-star-systems-and-bodies-3.md) — beginner

### [Move a planet or moon at runtime by swapping its orbit or changing its parent](howto/celestials/systems-and-stars/move-or-reparent-a-celestial-body/overview.md)

Bodies are on rails, so moving one means giving it a new Orbit at a moment the orbit solver will pick it up, then repairing the Children lists, cached transforms and subtree by hand; and restoring the original orbit afterwards

- [SetOrbit plus manual Children surgery and UpdatePerFrameDataTree, with cycle guards on replay](howto/celestials/systems-and-stars/move-or-reparent-a-celestial-body/move-or-reparent-a-celestial-body-1.md) — advanced
- [Priority.First prefix on ExecuteNextVehicleSolvers builds a fresh orbit from the target's state vectors](howto/celestials/systems-and-stars/move-or-reparent-a-celestial-body/move-or-reparent-a-celestial-body-2.md) — advanced

### [Place starting vessels in a custom system with reusable situations](howto/celestials/systems-and-stars/place-starting-vessels-in-custom-systems/overview.md)

Register orbital Situation assets and reference them from library-loaded starter vessels in a native system XML

- [Situation assets referenced by a library-loaded starter vessel](howto/celestials/systems-and-stars/place-starting-vessels-in-custom-systems/place-starting-vessels-in-custom-systems.md) — intermediate

### [Read KSA's star binary files and reload or replace the star field at runtime](howto/celestials/systems-and-stars/read-and-replace-star-binaries/overview.md)

The 16-byte star record format named by mod.toml, how to read it for catalog joins, and how to swap the star field at runtime by rewriting Mod.StarBinaries, resetting the instanced star technique and refilling it within its capacity

- [Mirror Mod.LoadStarBinaries when parsing](howto/celestials/systems-and-stars/read-and-replace-star-binaries/read-and-replace-star-binaries-1.md) — beginner
- [Reflect Mod.StarBinaries, reset and refill the star technique, confirm through a game Popup](howto/celestials/systems-and-stars/read-and-replace-star-binaries/read-and-replace-star-binaries-2.md) — advanced

### [Read a body's atmosphere, ocean, ring, rotation, tilt, tidal-lock and orbit-element data](howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/overview.md)

Where each per-body number lives in KSA (IParentBody, the body template, transforms and the orbit), how to read atmosphere and ocean data that may be null, sample density and pressure by altitude, and the corrections moons need for tilt and inclination

- [IParentBody.GetAtmosphereReference and GetOceanReference projected into a snapshot](howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-1.md) — beginner
- [Atmosphere height, density and ring templates feeding classification decisions, and angular velocity to a stationary orbit radius](howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-2.md) — intermediate
- [Sample the live density and pressure model by altitude](howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-3.md) — beginner
- [Template and transform reads for tilt, periods and inclination](howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-4.md) — intermediate
