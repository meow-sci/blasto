# Game Feature

* [Read a vehicle's position, velocity, attitude, orbit and flight instruments](overview.md) - Which Vehicle members hold kinematics, orbit, navball, environment and encounter data, which frame and unit each uses, when a read is valid, and how to derive altitude, speed, g-load, dynamic pressure and Mach

# How-To

* [Build an immutable flight-state snapshot in a main-viewport OnFrame prefix](read-vehicle-flight-state-1.md) - Copy position, velocity, attitude, orbit, navball, environment and SOI encounters out of Vehicle into a record that other threads can read, scrubbing NaN and the UniverseTime sentinels
* [Derive Mach number from the game's atmosphere pressure and density](read-vehicle-flight-state-4.md) - Compute the speed of sound as sqrt(1.4 p / rho) from PhysicsEnvironment, understand why KSA's isothermal atmosphere makes it constant per body, and use it from a physics patch or the main thread
* [Read altitude, speed, vertical speed, g-load and Mach in a Harmony postfix for an instrument panel](read-vehicle-flight-state-3.md) - Read a settled vehicle inside a postfix on Program.OnFrameEditor to feed a HUD, and derive barometric versus radar altitude, surface speed, vertical speed, g-load and Mach correctly
* [Take a plain flight-state snapshot in StarMapAfterOnFrame and draw it next frame](read-vehicle-flight-state-2.md) - Refill one snapshot object per frame from the controlled vehicle after the game has finished the frame, and add thrust, thrust-to-weight, dynamic pressure and downrange to the basic reads
