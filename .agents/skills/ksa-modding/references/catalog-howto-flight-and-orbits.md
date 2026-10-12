# Flight and orbits

Reading a vehicle's flight state and orbit, working in KSA's coordinate frames and epochs, driving the flight computer and burns from a mod, extending the stock Transfer Planner, transfer and encounter math, targets, map-view overlays and guided flight (ascent, boostback, landing, ballistic and homing guidance).

## Flight state

Reading where a craft is and how it flies: coordinate frames and epochs, flight state, horizon-frame angles, orbit elements, mission time and simulation speed.

### [Show mission elapsed time from actual liftoff and pick the right clock under time warp](howto/flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)

Start a mission clock at real liftoff instead of scene load, and choose between universe time, the applied simulation step and wall clock so timers, schedulers and cooldowns behave under time warp

- [Run a T-/T+ mission clock keyed on LaunchGameTime with contact-loss liftoff detection](howto/flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/compute-mission-elapsed-time-and-detect-liftoff-1.md) — intermediate
- [Advance simulated objects on Universe.GetLastSimStep, deduplicated on NextTime](howto/flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/compute-mission-elapsed-time-and-detect-liftoff-2.md) — advanced
- [Choose render, wall or sim time for timed command playback](howto/flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/compute-mission-elapsed-time-and-detect-liftoff-3.md) — intermediate

### [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](howto/flight-and-orbits/flight-state/read-flight-state-in-a-local-horizon-frame/overview.md)

How to express a vehicle's attitude and velocity relative to the local east-north-up horizon, using the game's own ENU frame helpers and quaternion type, and how to derive angle of attack and sideslip

- [Concatenate GetBody2Cci with the inverse ENU frame and call ToRollPitchYawRadians](howto/flight-and-orbits/flight-state/read-flight-state-in-a-local-horizon-frame/read-flight-state-in-a-local-horizon-frame-1.md) — intermediate
- [Transform basis vectors with the game's own quaternion type to build a site-frame attitude](howto/flight-and-orbits/flight-state/read-flight-state-in-a-local-horizon-frame/read-flight-state-in-a-local-horizon-frame-2.md) — advanced

### [Read orbit elements, apsides and event countdowns safely](howto/flight-and-orbits/flight-state/read-orbit-elements-and-timing/overview.md)

Read a vehicle's or celestial's orbit elements, apsides, periods and event countdowns, and choose the right Mu for transfer maths, without tripping on hyperbolic orbits, NaN or UniverseTime sentinels

- [Compute orbit readouts and event countdowns from state vectors in CCI](howto/flight-and-orbits/flight-state/read-orbit-elements-and-timing/read-orbit-elements-and-timing-1.md) — intermediate
- [Read orbit and encounter data without leaking NaN or UniverseTime sentinels](howto/flight-and-orbits/flight-state/read-orbit-elements-and-timing/read-orbit-elements-and-timing-2.md) — intermediate
- [Choose Orbit.Mu or Parent.Mu for transfer maths and cache per loaded system](howto/flight-and-orbits/flight-state/read-orbit-elements-and-timing/read-orbit-elements-and-timing-3.md) — intermediate

### [Read a vehicle's position, velocity, attitude, orbit and flight instruments](howto/flight-and-orbits/flight-state/read-vehicle-flight-state/overview.md)

Which Vehicle members hold kinematics, orbit, navball, environment and encounter data, which frame and unit each uses, when a read is valid, and how to derive altitude, speed, g-load, dynamic pressure and Mach

- [Build an immutable flight-state snapshot in a main-viewport OnFrame prefix](howto/flight-and-orbits/flight-state/read-vehicle-flight-state/read-vehicle-flight-state-1.md) — intermediate
- [Take a plain flight-state snapshot in StarMapAfterOnFrame and draw it next frame](howto/flight-and-orbits/flight-state/read-vehicle-flight-state/read-vehicle-flight-state-2.md) — beginner
- [Read altitude, speed, vertical speed, g-load and Mach in a Harmony postfix for an instrument panel](howto/flight-and-orbits/flight-state/read-vehicle-flight-state/read-vehicle-flight-state-3.md) — intermediate
- [Derive Mach number from the game's atmosphere pressure and density](howto/flight-and-orbits/flight-state/read-vehicle-flight-state/read-vehicle-flight-state-4.md) — beginner

### [Set, limit and request simulation speed from a mod](howto/flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md)

Change the time-warp rate from code, request a warp to a time through the game's own auto-warp buffer, and clamp the speed during phases that cannot tolerate warp

- [Set the speed with Universe.SetSimulationSpeed and suppress the on-screen alert](howto/flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/set-simulation-speed-from-a-mod-1.md) — beginner
- [Request a warp-to-time through InputEvents.AutoWarpBuffer](howto/flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/set-simulation-speed-from-a-mod-2.md) — intermediate
- [Hold the speed down with a feedback controller over SetSimulationSpeed, with player yield and restore](howto/flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/set-simulation-speed-from-a-mod-3.md) — advanced

### [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)

Which frame and epoch each Vehicle, Orbit, Celestial and Camera member lives in, how to sample positions at one consistent instant and how to convert a position to latitude, longitude and altitude

- [Vessel state in CCI, CCF and ECL: which member is in which frame](howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-1.md) — intermediate
- [Use Ecl as the working frame, Ego for anything drawn, and cancel the 29.8 km/s carrier](howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-2.md) — advanced
- [Store anchors in the owner's frame and resolve them to ECL on use](howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-3.md) — intermediate
- [Equatorial versus ecliptic reference planes for inclination](howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-4.md) — advanced
- [Pair the rendered Ego position with ECL offsets from the same instant](howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-5.md) — intermediate

## Autopilot

Flying a craft from code: maneuver nodes, executing burns, the flight computer and attitude modes, per-vehicle autopilots and navigation targets.

### [Create, chain and read stock maneuver nodes from code](howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/overview.md)

Turn a computed impulse into a stock maneuver node, compute set-apsis, circularize and plane-change burns, place a chain of burns on the orbits earlier burns produce, read back planned burns and draw them on a timeline

- [Create a stock maneuver node with Burn.Create and queue it through InputEvents.BurnUpdateBuffer](howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-1.md) — intermediate
- [Schedule an impulsive burn directly on the flight computer from a solver-phase prefix](howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-2.md) — advanced
- [Compute set-apsis, circularize and plane-change impulses and plan them after the last pending burn](howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-3.md) — advanced
- [Chain burns with CalculateBurnPatch so every later pass sits on the orbit the earlier ones produce](howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-4.md) — advanced
- [Read planned burns with a TryGetBurn loop and draw them on a mission timeline](howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-5.md) — beginner

### [Drive the flight computer and attitude modes from a mod](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)

Write a Custom attitude target so the stock controller flies a mod-computed attitude, command gimbals, and pick the write window that is not erased, raced or seen a step late

- [Orient a vehicle to a sky direction from a Vehicle.PrepareWorker prefix](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-1.md) — intermediate
- [Command other vehicles through queued writes performed in a PrepareWorker prefix](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-2.md) — advanced
- [Set attitude mode and an arbitrary quaternion from a solver-phase command drain](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-3.md) — advanced
- [Custom attitude in an inertial frame plus turning-rate feedforward, gimbal commands and thrust inversion](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-4.md) — advanced
- [Write actuator commands from one worker-thread postfix on FlightComputer.ComputeControl](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-5.md) — advanced
- [Run a main-thread tick around Universe.ApplyVehicleSolvers](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-6.md) — intermediate
- [Edit ComputeControl inputs by ref in a prefix and restore them in a finalizer](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-7.md) — advanced
- [Queue vehicle changes in your own ConcurrentQueue and drain it in an ApplyInputEvents prefix](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-8.md) — intermediate
- [Patch every attitude-mode writer so the player can take back control](howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-9.md) — advanced

### [Execute burns with RCS translation and throttle inputs from code](howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/overview.md)

Drive throttle and RCS translation inputs from a mod, execute a burn with RCS only, and trim and release several payloads along one line from a bus

- [Intercept the Auto burn-mode request and fly the burn with RCS only](howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-1.md) — advanced
- [Trim a bus with one translation direction at a time, then go quiet so KSA puts it back on rails](howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-2.md) — advanced
- [Write throttle and RCS flags by reflection into Vehicle._manualControlInputs](howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-3.md) — intermediate
- [Drive throttle and translation through Vehicle.ProcessInput, re-applied from the PrepareWorker window](howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-4.md) — intermediate

### [Execute planned burns hands-free with the stock Auto burn](howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/overview.md)

Split a long burn into several passes, hand each computed maneuver to the stock Auto burn, detect which planned burn the flight computer just finished, and remove completed burns from the plan so a sequence runs without the player

- [Run a multi-pass burn sequence as a state machine on stock Auto, persisted per vehicle](howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-1.md) — advanced
- [Allocate a long burn across passes from a private staging model](howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-2.md) — advanced
- [Let stock compute the ignition timing before you arm Auto](howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-3.md) — advanced
- [Detect the end of a stock burn with a RaisePendingAlerts prefix and postfix](howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-4.md) — intermediate
- [Remove the last finished burn from the plan with a RaisePendingAlerts postfix](howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-5.md) — beginner

### [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/overview.md)

Run a per-vehicle guidance loop on the main thread right before each vehicle's worker snapshot, claim vehicles per mod, detect when the player takes over attitude or burn mode, and release only what you changed

- [Step a per-vehicle autopilot from a PrepareWorker prefix with takeover checks and a menu off switch](howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/run-an-autopilot-for-every-vehicle-each-step-1.md) — advanced
- [Adopt a separated booster as its own guided vehicle](howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/run-an-autopilot-for-every-vehicle-each-step-2.md) — advanced
- [Fly a vehicle that is not the controlled one, then hand it back](howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/run-an-autopilot-for-every-vehicle-each-step-3.md) — advanced
- [Claim vehicles per mod, hold burn mode and compare exact fields to detect a takeover](howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/run-an-autopilot-for-every-vehicle-each-step-4.md) — advanced

### [Set or clear a vehicle's target, including bodies the game cannot target](howto/flight-and-orbits/autopilot/set-and-clear-the-vehicle-target/overview.md)

Set and clear the vehicle target from mod code through the same queued path the UI uses, and make fixed stars and barycenters selectable as a navigation-only target the stock target list refuses

- [Set and clear the target with Universe.SetTargetCommand and UnsetTargetCommand](howto/flight-and-orbits/autopilot/set-and-clear-the-vehicle-target/set-and-clear-the-vehicle-target-1.md) — beginner
- [Make fixed stars and barycenters targetable with weak tables keyed by Vehicle and BurnPlan](howto/flight-and-orbits/autopilot/set-and-clear-the-vehicle-target/set-and-clear-the-vehicle-target-2.md) — advanced

### [Synthesize a NavigationTarget for an object that has no orbit](howto/flight-and-orbits/autopilot/synthesize-a-navigation-target-for-the-flight-computer/overview.md)

Build the NavigationTarget struct the navball, flight computer and Toward/Away modes read for a side target, and make the solver thread and the navball see it only while it is needed

- [Inject a NavigationTarget into ComputeControl with a prefix, __state and a restoring finalizer](howto/flight-and-orbits/autopilot/synthesize-a-navigation-target-for-the-flight-computer/synthesize-a-navigation-target-for-the-flight-computer-1.md) — advanced
- [Transpile NavigationTarget.Create call sites and target-presence checks for the navball](howto/flight-and-orbits/autopilot/synthesize-a-navigation-target-for-the-flight-computer/synthesize-a-navigation-target-for-the-flight-computer-2.md) — advanced

## Transfers

Transfer planning: patched-conic delta-v, multi-leg routes, transfer and launch windows, flyby targeting and extending the stock Transfer Planner.

### [Add plan types to the stock Transfer Planner](howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/overview.md)

Extend the stock Transfer Planner window with a custom plan type by registering it, injecting UI and call-site changes into the plan window, opening and preselecting the planner from a mod, and letting it target bodies on hyperbolic orbits

- [Register plan types and redraw the stock window in a skip-original prefix under the same id, title and signature](howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-1.md) — advanced
- [Inject UI and swap a call in DrawPlanWindow with a transpiler that validates its IL anchors](howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-2.md) — advanced
- [Open and preselect the stock Transfer Planner with AccessTools FieldRefs and delegates bound once](howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-3.md) — intermediate
- [Hand a retargeted transfer to the stock Create path](howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-4.md) — advanced
- [Let the planner target hyperbolic bodies with an XML patch, NaN-period patches and a Harmony finalizer](howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-5.md) — advanced

### [Aim a transfer at a flyby periapsis and predict its encounter](howto/flight-and-orbits/transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)

Target a chosen periapsis around a destination body instead of its center by solving the transfer with Lambert, predict the encounter periapsis of a planned burn, and iterate the departure burn until the miss is closed

- [Offset the Lambert aim point by an impact parameter and hand the result to the planner's Create path](howto/flight-and-orbits/transfers/aim-a-transfer-at-a-flyby-periapsis/aim-a-transfer-at-a-flyby-periapsis-1.md) — advanced
- [Predict a planned burn's encounter with CalculateBurnPatch and ComputeCompleteTrajectory filtered to the target](howto/flight-and-orbits/transfers/aim-a-transfer-at-a-flyby-periapsis/aim-a-transfer-at-a-flyby-periapsis-2.md) — advanced
- [Solve your own universal-variable Lambert against a spinning-planet target](howto/flight-and-orbits/transfers/aim-a-transfer-at-a-flyby-periapsis/aim-a-transfer-at-a-flyby-periapsis-3.md) — advanced

### [Build a local site frame and compute a launch window into a target plane](howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)

Build a local east-north-up frame at a surface site on a rotating body, get surface-relative velocity and terrain height in it, and find when a launch site passes under a target orbit plane and which azimuth to fly

- [Find the launch window into a target plane from site latitude, inclination and a pad lead](howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/build-a-site-frame-and-compute-a-launch-window-1.md) — advanced
- [Build a flat site frame from CCI and take surface-relative velocity as v minus omega cross r](howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/build-a-site-frame-and-compute-a-launch-window-2.md) — advanced
- [Take north from the body's spin axis and query terrain height in the same frame](howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/build-a-site-frame-and-compute-a-launch-window-3.md) — intermediate
- [Read orbit-relative surface velocity as radial and tangential parts](howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/build-a-site-frame-and-compute-a-launch-window-4.md) — beginner

### [Compute phase angles, synodic periods and the time to the next transfer window](howto/flight-and-orbits/transfers/compute-transfer-windows/overview.md)

Find the phase angle and the time until the next transfer window between two bodies using the game's AlignmentTime branches and Orbit.GetPhaseAngle, and schedule a multi-pass transfer around it

- [Mirror the branches of OrbitalTransfers.AlignmentTime with a live phase clock](howto/flight-and-orbits/transfers/compute-transfer-windows/compute-transfer-windows-1.md) — intermediate
- [Shift a same-parent window and re-solve Lambert for a multi-pass Hohmann departure](howto/flight-and-orbits/transfers/compute-transfer-windows/compute-transfer-windows-2.md) — advanced

### [Estimate transfer delta-v in closed form with patched conics](howto/flight-and-orbits/transfers/estimate-transfer-delta-v-with-patched-conics/overview.md)

Compute Hohmann-style departure and arrival delta-v between circular reference orbits around one body and across spheres of influence with the patched-conic approximation and the game's own orbit and Mu data

- [Estimate a coplanar Hohmann transfer to a circular altitude from the current orbit](howto/flight-and-orbits/transfers/estimate-transfer-delta-v-with-patched-conics/estimate-transfer-delta-v-with-patched-conics-1.md) — beginner
- [Price transfers with hub Hohmann legs wrapped in Oberth burns, cached per body pair](howto/flight-and-orbits/transfers/estimate-transfer-delta-v-with-patched-conics/estimate-transfer-delta-v-with-patched-conics-2.md) — advanced

### [Plan and total multi-leg and interstellar delta-v routes](howto/flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/overview.md)

Classify a vessel's orbit state, derive a reference orbit for each body, find a route through the body tree, sum the leg delta-vs, and estimate fuel and coast time for an interstellar trip

- [Find a delta-v route through a re-rooted body tree and total it with a display-time margin](howto/flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/plan-multi-leg-and-interstellar-delta-v-routes-1.md) — intermediate
- [Build a per-body state ladder and classify the vessel onto it with a snapped eccentricity rule](howto/flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/plan-multi-leg-and-interstellar-delta-v-routes-2.md) — intermediate
- [Price an interstellar leg by reusing the stock burn model, with a golden-section speed search and a rocket-equation fallback](howto/flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/plan-multi-leg-and-interstellar-delta-v-routes-3.md) — advanced

## Guidance

Guided flight: suicide burns, powered landing, closed-loop ascent, boostback and glide, ballistic prediction, homing guidance and running heavy solves off the frame.

### [Compute a suicide-burn start altitude and the thrust it needs](howto/flight-and-orbits/guidance/compute-a-suicide-burn/overview.md)

Work out when to light the engines for a just-in-time landing burn from live thrust, mass flow and gravity, and invert a thrust demand into a throttle setting

- [Closed-form suicide-burn calculator on radar altitude](howto/flight-and-orbits/guidance/compute-a-suicide-burn/compute-a-suicide-burn-1.md) — intermediate
- [Sum ComputeActivePerformance thrust per active controller for a live thrust and T/W readout](howto/flight-and-orbits/guidance/compute-a-suicide-burn/compute-a-suicide-burn-2.md) — beginner
- [Evaluate the core engine model at a throttle and pressure, and invert a thrust demand into a throttle](howto/flight-and-orbits/guidance/compute-a-suicide-burn/compute-a-suicide-burn-3.md) — advanced

### [Fly a boostback burn and glide a booster to its landing site](howto/flight-and-orbits/guidance/fly-a-boostback-and-glide-to-a-landing-site/overview.md)

Fly a boostback burn and then steer an unpowered descent onto a landing site with angle of attack

- [Adopt a separated booster and fly a boostback burn from impact predictions](howto/flight-and-orbits/guidance/fly-a-boostback-and-glide-to-a-landing-site/fly-a-boostback-and-glide-to-a-landing-site-1.md) — advanced
- [Glide a booster onto its site with a zero-effort-miss law and an inverted lift curve](howto/flight-and-orbits/guidance/fly-a-boostback-and-glide-to-a-landing-site/fly-a-boostback-and-glide-to-a-landing-site-2.md) — advanced

### [Fly a closed-loop ascent to a target orbit](howto/flight-and-orbits/guidance/fly-closed-loop-ascent-guidance/overview.md)

Guide a launch to a target orbit with closed-loop ascent guidance, optionally seeded by a propellant-optimal pre-planned trajectory

- [Fly a UPFG ascent using a stage list built from the game's SequencePerformance phases](howto/flight-and-orbits/guidance/fly-closed-loop-ascent-guidance/fly-closed-loop-ascent-guidance-1.md) — advanced
- [Plan a propellant-optimal ascent with SCvx on a private thread and fly the published plan](howto/flight-and-orbits/guidance/fly-closed-loop-ascent-guidance/fly-closed-loop-ascent-guidance-2.md) — advanced

### [Plan and fly a powered landing](howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/overview.md)

Plan a deorbit burn to a site on a rotating body, fly a fuel-optimal powered descent, and finish with a hover and touchdown under engine power

- [Fly a fuel-optimal descent with G-FOLD solved synchronously under a time limit](howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-1.md) — advanced
- [Fly a 6-DOF landing MPC with the solve on a dedicated background thread](howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-2.md) — advanced
- [Hover and touch down with a sink-profile velocity controller and terrain height below the craft](howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-3.md) — intermediate
- [Plan a deorbit burn to a landing site with a time-sliced Lambert search, then let a stock node fly it](howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-4.md) — advanced

### [Predict impact points with drag and build a ballistic computer](howto/flight-and-orbits/guidance/predict-impact-point-and-build-a-ballistic-computer/overview.md)

Predict where a falling vehicle will hit the ground, measure lift and drag under the active aero model, and compute the velocity to be gained for a powered flight to a ballistic target

- [Predict an impact point with gravity, drag and a measured drag table](howto/flight-and-orbits/guidance/predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-1.md) — advanced
- [Predict where a released store lands by flying a throwaway round in a ground-carried frame](howto/flight-and-orbits/guidance/predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-2.md) — advanced
- [Build a ballistic computer with a closed-form aim and a flown impact check](howto/flight-and-orbits/guidance/predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-3.md) — advanced

### [Run an expensive solve without stalling the frame](howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md)

Move a heavy guidance or planning solve (convex ascent, descent MPC, deorbit planner, guidance preview) off the frame and the vehicle worker, and pick it up when it finishes

- [Build plain data on the main thread, solve on a worker, publish an immutable plan](howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-1.md) — advanced
- [Receding-horizon MPC on a worker with a wall-clock budget and one solver instance per thread](howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-2.md) — advanced
- [Solve a cheaper problem synchronously inside the vehicle step with a solver time limit](howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-3.md) — intermediate
- [Time-slice a search across frames with a C# iterator and per-step and total work budgets](howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-4.md) — intermediate
- [Rate-limit a solve on the wall clock and cache results per object with real and simulated time limits](howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-5.md) — beginner

### [Steer by predicted miss (zero-effort-miss guidance) and model homing sensors](howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/overview.md)

Steer a missile or falling object by its line-of-sight rate, its zero-effort miss or its predicted impact, and model a sensor's field of view, line of sight and target loss for homing objects

- [Steer a mod-simulated round by true proportional navigation on a back-dated target sample](howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-1.md) — advanced
- [Steer on a predicted impact point with a zero-effort-miss law](howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-2.md) — advanced
- [Steer a falling store by its predicted impact and take a ground designation as its input](howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-3.md) — advanced
- [Model a homing sensor's gimbal limit, raycast occlusion, decoys and uplink loss](howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-4.md) — advanced

## Map view

Map-view interaction: closest approach, points anchored to bodies, and predicted trajectories, markers and orbit lines.

### [Anchor points to a rotating body and pick bodies and orbit lines under the cursor](howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/overview.md)

Keep a point attached to a rotating, orbiting body so it stays put on the surface or on an orbit, and find the body, limb or orbit-line point under the mouse cursor

- [Click a point on the terrain and keep it as a body-fixed anchor re-read into ecliptic position and velocity every frame](howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-1.md) — intermediate
- [Store points as immutable anchors in CCE, orbit-parent or body-fixed frames, prune them by identity and clear them on save load](howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-2.md) — intermediate
- [Pick a body under the cursor by its projected centre and disc radius, and snap to its limb by the closest point on the cursor ray](howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-3.md) — intermediate
- [Pick the point on an orbit line under the cursor with Orbit.GetNearestPosition and re-validate it on screen](howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-4.md) — intermediate

### [Draw a predicted trajectory and markers in the map view, and control orbit lines](howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/overview.md)

Preview a planned burn's trajectory, multi-pass previews and impact sights on screen, draw map-view markers, arrows and overlays, and override whether a body's orbit line is drawn and recolour it

- [Preview a burn's trajectory with a real FlightPlan from OrbitalTransfers.BuildFlightPlan, drawn from a TransferPlanner.OnPreRender postfix](howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-1.md) — advanced
- [Preview several planned passes as dimmed orbit lines with custom markers and a quantized preview cache](howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-2.md) — advanced
- [Draw a flown prediction as an ImGui polyline on the background draw list, anchored to the craft and broken behind the camera](howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-3.md) — intermediate
- [Project orbit positions to the map view with GetPositionEclFromCce and EclToScreen and draw markers and fixed-length arrows](howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-4.md) — intermediate
- [Draw CCI and body-fixed points through a click-through full-screen overlay window with per-viewport projection and fault isolation](howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-5.md) — advanced
- [Force or hide a body's orbit line and change its colour with a postfix on Astronomical.ShouldDrawLines and Orbit.OrbitLineColor](howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-6.md) — intermediate

### [Find the closest approach to a target along a trajectory](howto/flight-and-orbits/map-view/find-closest-approach-along-the-flight-plan/overview.md)

Locate the time and distance of closest approach to a target along the flight plan or a straight-line prediction, classify what is coming close, and write the result into the stock gauge digits

- [Search the flight plan for the closest approach with uniform and log-spaced samples and golden-section refinement, then write it into the stock TGT gauge](howto/flight-and-orbits/map-view/find-closest-approach-along-the-flight-plan/find-closest-approach-along-the-flight-plan-1.md) — intermediate
- [Compute the closest point of approach from straight-line relative motion and rank contacts by time to it](howto/flight-and-orbits/map-view/find-closest-approach-along-the-flight-plan/find-closest-approach-along-the-flight-plan-2.md) — beginner
