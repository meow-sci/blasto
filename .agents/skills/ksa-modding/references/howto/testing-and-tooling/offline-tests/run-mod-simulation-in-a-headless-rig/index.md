# Game Feature

* [Run the mod's whole simulation in a headless rig behind a sim boundary](overview.md) - Drive vehicles, rounds or balls through the mod's own step function in a plain test with a rig that supplies mass, inertia and a terrain stand-in, and vary the step to expose phase and epoch errors

# How-To

* [Fly a rigid-body rig through the mod's own drive step with the engine's mass and inertia](run-mod-simulation-in-a-headless-rig-1.md) - Test a custom vehicle model by integrating a minimal rigid body that uses the mass, inertia and centre of mass KSA derives from the part XML, then assert settling, steering and braking behaviour
* [Share one driver seam between the game and the headless rig and test with varying steps](run-mod-simulation-in-a-headless-rig-2.md) - Advance mod-simulated rounds through a single function that both the game and the tests call, so a failing test is a failing game, and vary the frame step so epoch and phase errors cannot hide behind a constant dt
* [Test ball and contact physics with an epoch bookkeeping layer and missed-frame invariance](run-mod-simulation-in-a-headless-rig-3.md) - Run a body-fixed ball and a swept contact rule on a planet-sized sphere in plain tests, then prove the frame latch, the step gate and the draw anchor survive skipped frames and varying steps
