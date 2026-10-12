# Physics

Forces, drag and aero, wheels and suspension, colliders, Bepu, custom motion, terrain contact and physics-safe mutation of simulated state, including patches inside the worker-thread vehicle solver.

## Solver

Writing simulated state safely: the physics-safe windows, worker-thread patches, injected forces and impulses, and keeping pushed craft off rails.

### [Inject forces, impulses and velocity changes at the physics safe point](howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)

How a mod pushes a craft (driven car, blast, boost, hover, lift, flip) by writing velocity, spin or pose deltas where they are integrated, and how to keep those pushes bounded at any time warp

- [Add velocity deltas in the PrepareWorker prefix](howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-1.md) — intermediate
- [Summed per-part drag-style shove with torque from a blast front](howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-2.md) — advanced
- [Impulsive kick from the solver-dispatch window](howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-3.md) — intermediate
- [Velocity and spin deltas gated by hatches in the PrepareWorker prefix](howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-4.md) — advanced
- [Hover and touchdown under real engine power with a PID](howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-5.md) — advanced

### [Keep a craft under full physics while your mod pushes it](howto/physics/solver/keep-a-pushed-craft-off-rails/overview.md)

Why a railed craft ignores velocity writes, which per-craft checks decide rails status, and two ways to keep a pushed craft in the physics bubble

- [Postfix PhysicsBubble.KittenWantsWake from a concurrent set](howto/physics/solver/keep-a-pushed-craft-off-rails/keep-a-pushed-craft-off-rails-1.md) — advanced
- [Request wake or off-rails from the flight computer command sink](howto/physics/solver/keep-a-pushed-craft-off-rails/keep-a-pushed-craft-off-rails-2.md) — advanced

### [Mutate vehicles at a physics-safe point](howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)

Why vehicle state written from a StarMap hook or ImGui button is lost, and the four windows where a write survives

- [Write inside a Vehicle.PrepareWorker prefix](howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md) — intermediate
- [Drain a command queue in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers](howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md) — intermediate
- [Run a callback at the PrepareFrame handoff with a GetJobSimStep transpiler](howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md) — advanced
- [Join JobSystems.VehicleSolver before mutating from a frame hook](howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-4.md) — intermediate

### [Write worker-thread-safe patches inside the vehicle solver](howto/physics/solver/write-worker-thread-safe-solver-patches/overview.md)

How to share data with Harmony patches that run on vehicle worker threads per substep, without locks, allocation or exceptions

- [Pure, allocation-free, never-throwing code over the in arguments](howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-1.md) — advanced
- [Read-only tables filled on the main thread before physics starts](howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-2.md) — intermediate
- [Concurrent registries keyed by reference](howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-3.md) — intermediate
- [Volatile snapshot handoff from worker patches to the main thread](howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-4.md) — advanced
- [Identify the vehicle a callback runs for with a ThreadStatic set in a prefix and cleared in a finalizer](howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-5.md) — advanced
- [Immutable commands published before the solver job is queued](howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-6.md) — advanced

## Mod simulation

Bodies the mod simulates itself: sub-stepped integration, tunnelling-free hits, ray casts against hulls and terrain, and drag and gravity.

### [Apply air drag, gravity and aiming to a mod-simulated body](howto/physics/mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/overview.md)

How a mod gives its own integrated ball, shell or missile the same air, water and gravity the game uses, and aims a moving target with the same model

- [Density ratio to one reference air, ocean density below the waterline](howto/physics/mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/apply-drag-and-gravity-to-a-mod-simulated-body-1.md) — intermediate
- [Shell and missile drag with the same density read](howto/physics/mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/apply-drag-and-gravity-to-a-mod-simulated-body-2.md) — advanced
- [Impact prediction in a co-rotating atmosphere](howto/physics/mod-simulation/apply-drag-and-gravity-to-a-mod-simulated-body/apply-drag-and-gravity-to-a-mod-simulated-body-3.md) — advanced

### [Detect hits for fast bodies without tunnelling](howto/physics/mod-simulation/prevent-tunnelling-of-fast-bodies/overview.md)

How a mod finds the moment of contact for small fast objects by solving closest approach, hull casts or swept shapes over each sub-step instead of testing end points

- [Analytic closest approach t = -r.v / v.v clamped to the step](howto/physics/mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-1.md) — intermediate
- [Sweep against a bounding sphere, then cast the hull triangles](howto/physics/mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-2.md) — advanced
- [Swept circle against grown boxes, posts and a regulation cup](howto/physics/mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-3.md) — advanced

### [Ray cast against part meshes and terrain, from the cursor or any origin](howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)

How a mod casts a ray or path segment against KSA's part meshes, kittens, cloth and the terrain height field, from the mouse cursor or from an arbitrary origin, to hit-test, place, launch or measure

- [Round-centred ego origin and sphere broad phase](howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-1.md) — advanced
- [Cursor ray against every vehicle, plus the stock hover picker](howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-2.md) — intermediate
- [Per-triangle cast in a mover-centred frame with no camera](howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-3.md) — advanced
- [Walk the ray over the height field in bounded steps](howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-4.md) — advanced
- [Layered cast over cloth, part meshes, kitten sphere and a terrain march](howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-5.md) — advanced
- [Frame-budgeted sweeps of the craft's own geometry for actuator travel limits](howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-6.md) — advanced

### [Simulate mod-owned bodies in sub-steps outside the vehicle system](howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)

How a mod runs missiles, shells or a golf ball as plain objects integrated in fixed sub-steps on KSA's simulated time, with the right frame, epoch and terrain contact

- [IProjectile objects with fixed 5 ms sub-steps and a faithful-step clamp](howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-1.md) — advanced
- [Ball integrated at up to 1/240 s in the body-fixed frame](howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-2.md) — intermediate
- [Deduplicate GetLastSimStep on NextTime and widen to the span since the last boundary](howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-3.md) — intermediate
- [Ecl state at GetElapsedTime with epoch back-dating](howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-4.md) — advanced
- [Read GetStatesCcf in the PrepareWorker prefix](howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-5.md) — advanced
- [Differences-only inputs and headless tests](howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-6.md) — intermediate

## Colliders

Bepu colliders and contacts: the shared shape registry, static colliders, clutter colliders, collision suppression, collider scaling, moving platforms and a private Bepu world.

### [Add mod-owned static colliders that vessels and kittens collide with](howto/physics/colliders/add-static-colliders-to-physics-bubbles/overview.md)

How a mod makes placed models or course geometry solid, by adding Bepu statics to every nearby physics bubble each pass, or by declaring XML static objects, and why the stock contact filter must be taught to admit them

- [Per-bubble static handles from a BeginStaticObjectPass postfix](howto/physics/colliders/add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-1.md) — advanced
- [Worker prefixes on DetectCollisions and Simulate with statics registered as ground clutter](howto/physics/colliders/add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-2.md) — advanced
- [Declare XML static objects and let the engine collide the nearest pad](howto/physics/colliders/add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-3.md) — intermediate

### [Build a moving platform or elevator kittens can ride](howto/physics/colliders/build-a-moving-platform-kittens-can-ride/overview.md)

How a mod moves a collider-only static object by rewriting its template GroundOffset each simulated step under the contact recovery speed, and wakes sleeping riders so they follow

- [Rewrite GroundOffset of a dedicated launch-pad object](howto/physics/colliders/build-a-moving-platform-kittens-can-ride/build-a-moving-platform-kittens-can-ride.md) — advanced

### [Use ground clutter as colliders: custom shapes and vehicle interaction](howto/physics/colliders/give-ground-clutter-custom-colliders/overview.md)

How a mod swaps a planet's ground clutter for its own collidable objects, builds the colliders from KSA's ColliderTemplate types, switches clutter collisions on and tunes how easily rocks come loose

- [Subclass ColliderTemplate with an owned convex hull](howto/physics/colliders/give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-1.md) — advanced
- [Swap clutter bundles in an ExecuteNextClothSolvers prefix](howto/physics/colliders/give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-2.md) — advanced
- [GroundClutterCollisions setting and DisplaceEnergyPerKg](howto/physics/colliders/give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-3.md) — intermediate
- [Register course statics as clutter on every pass](howto/physics/colliders/give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-4.md) — advanced

### [Create and free shapes in KSA's global Bepu shape registry](howto/physics/colliders/mutate-the-shared-bepu-shape-registry/overview.md)

Why ConstraintSim.UnlockShapes throws while the vehicle step runs, and the three ways mods create, cache and free Bepu shapes: at the dispatch window, after waiting on the solver, or by blocking until the step ends

- [Create shapes in an ExecuteNextVehicleSolvers prefix and cache by size](howto/physics/colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-1.md) — advanced
- [Add and free statics' shapes only while the solver is idle](howto/physics/colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-2.md) — advanced
- [UnlockShapesBlocking around vehicle construction](howto/physics/colliders/mutate-the-shared-bepu-shape-registry/mutate-the-shared-bepu-shape-registry-3.md) — advanced

### [Run your own Bepu simulation alongside KSA's](howto/physics/colliders/run-your-own-bepu-simulation/overview.md)

How a mod simulates loose props in a separate Bepu world with its own buffer pool and shapes, driven in the vessel's frame with apparent-force terms, and writes the poses onto subparts

- [Single-threaded Timestep with own BufferPool in the assembly frame](howto/physics/colliders/run-your-own-bepu-simulation/run-your-own-bepu-simulation.md) — advanced

### [Scale or move a vessel's colliders at runtime](howto/physics/colliders/scale-or-move-a-vessels-colliders-at-runtime/overview.md)

How a mod resizes collision geometry independently of render and mass, or stows part colliders in the hull and moves or toggles them, by overriding what ColliderModule receives or by writing its part-frame position and NeedsColliderUpdate

- [Override ColliderModule.SetScale and call UpdateCollisionGeometry](howto/physics/colliders/scale-or-move-a-vessels-colliders-at-runtime/scale-or-move-a-vessels-colliders-at-runtime-1.md) — advanced
- [Write PositionPartAsmb and NeedsColliderUpdate](howto/physics/colliders/scale-or-move-a-vessels-colliders-at-runtime/scale-or-move-a-vessels-colliders-at-runtime-2.md) — intermediate

### [Disable collisions for one vessel without touching others](howto/physics/colliders/suppress-collisions-for-one-vessel/overview.md)

How a mod takes a single vessel out of Bepu collision detection for the duration of each constraint-sim pass, by swapping its shape for an empty one and restoring it in a finalizer

- [BodyReference.SetShape(default) in a prefix with a restoring finalizer](howto/physics/colliders/suppress-collisions-for-one-vessel/suppress-collisions-for-one-vessel.md) — advanced

## Kinematics

Moving whole vessels by fiat: teleporting to an orbit or state vector, placing on a surface and welding one vessel to another.

### [Place a vessel on a surface at a latitude and longitude or on a structure](howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/overview.md)

How a mod sets a vessel down on a body's surface with Vehicle.TeleportToLocation, corrects its bounds-based clearance when the vessel is not nose-up, and lends a structure's surface height so a craft stands on a roof

- [Queue TeleportToLocation once per action](howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-1.md) — beginner
- [Transpile the GetInitialKinematicStateForLocation call for head-up bounds](howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-2.md) — advanced
- [Lend the pad template's SurfaceHeight around TeleportToLocation](howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-3.md) — advanced
- [Build one-part craft in code and set them on the ground](howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-4.md) — advanced

### [Teleport a vessel or EVA kitten to an orbit or state vector](howto/physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md)

How a mod moves a vessel, EVA kitten or debris to an arbitrary position and velocity by building an Orbit with Orbit.CreateFromStateCci and calling Vehicle.Teleport at a window where it survives the physics step

- [Teleport to CCI state vectors in the solver-dispatch window](howto/physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/teleport-a-vessel-by-building-a-new-orbit-1.md) — intermediate
- [Orbit solved backwards from a requested pass](howto/physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/teleport-a-vessel-by-building-a-new-orbit-2.md) — advanced
- [Teleport at the PrepareFrame handoff and fire at a clicked point](howto/physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/teleport-a-vessel-by-building-a-new-orbit-3.md) — advanced

### [Weld or anchor one vessel to another](howto/physics/kinematics/weld-one-vessel-to-another/overview.md)

How a mod pins a vessel to another vessel or part by re-teleporting it onto the anchor pose every frame, stamped with the right simulation time, with optional eased animation and named presets stored in TOML

- [BeforePhysics callback at the PrepareFrame handoff](howto/physics/kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-1.md) — advanced
- [VehicleSolver.Wait then teleport stamped with the next sim step](howto/physics/kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-2.md) — intermediate
- [Per-weld eased animation queues in player time](howto/physics/kinematics/weld-one-vessel-to-another/weld-one-vessel-to-another-3.md) — intermediate

## Aero and ground

The environment a craft flies and drives in: aerodynamic force models, atmosphere and ocean overrides, terrain height overrides, ground sampling and wheel suspension.

### [Override a body's atmosphere profile or ocean in the physics environment](howto/physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/overview.md)

How a mod makes the solver read its own air density and pressure profile for a body, or zeroes the ocean in a region so a basement can sit below the waterline

- [Prefix the density and pressure functions and move the private boundary height](howto/physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/override-atmosphere-and-ocean-in-the-physics-environment-1.md) — advanced
- [Postfix PhysicsEnvironment.RecomputePositionalValues on the worker](howto/physics/aero-and-ground/override-atmosphere-and-ocean-in-the-physics-environment/override-atmosphere-and-ocean-in-the-physics-environment-2.md) — advanced

### [Override the terrain height cameras and wheels see](howto/physics/aero-and-ground/override-terrain-height-for-roofs-and-platforms/overview.md)

How a mod makes wheel and camera code treat a roof, ramp or floor as the ground by postfixing the camera-frame terrain height query while leaving the fixed-frame query that physics collision reads

- [Postfix GetTerrainHeightFromDirCce keyed on who is asking](howto/physics/aero-and-ground/override-terrain-height-for-roofs-and-platforms/override-terrain-height-for-roofs-and-platforms.md) — advanced

### [Replace the stock aerodynamic force model and measure the active one](howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/overview.md)

How a mod swaps KSA's box drag for its own lift and drag on every vehicle inside the solver, computes Mach from the game atmosphere, and measures whatever model is active by calling the force function on a copied state

- [Call-site transpiler on ComputeDerivatives writing the Disturbances accumulator](howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-1.md) — advanced
- [Speed of sound from the atmosphere reference for a measurement tool](howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-2.md) — advanced

### [Sample terrain and pad surfaces under each wheel](howto/physics/aero-and-ground/sample-the-ground-under-each-wheel/overview.md)

How wheel or ground-contact code gets the true surface under a point, combining the accurate terrain height with the launch-pad and static-object surfaces the height field omits

- [GetTerrainHeightFromDirCce with accurate true in the PrepareWorker prefix](howto/physics/aero-and-ground/sample-the-ground-under-each-wheel/sample-the-ground-under-each-wheel-1.md) — intermediate
- [Copy convex children under the shapes lock in an ExecuteNextVehicleSolvers prefix](howto/physics/aero-and-ground/sample-the-ground-under-each-wheel/sample-the-ground-under-each-wheel-2.md) — advanced
- [Rebuild box and cylinder colliders from template data and drop a ray per wheel](howto/physics/aero-and-ground/sample-the-ground-under-each-wheel/sample-the-ground-under-each-wheel-3.md) — intermediate

### [Simulate wheels, suspension and tyres without a wheel module](howto/physics/aero-and-ground/simulate-wheels-and-suspension-without-a-wheel-module/overview.md)

How a mod holds a craft above the ground on per-wheel springs and grips it with friction-circle tyres when KSA has no wheel, tyre or suspension module

- [Raycast-style suspension impulses in the PrepareWorker prefix](howto/physics/aero-and-ground/simulate-wheels-and-suspension-without-a-wheel-module/simulate-wheels-and-suspension-without-a-wheel-module.md) — advanced
