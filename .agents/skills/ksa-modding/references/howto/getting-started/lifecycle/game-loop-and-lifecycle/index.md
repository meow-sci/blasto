# Game Feature

* [Game loop and mod lifecycle](overview.md) - How KSA starts, loads mods and runs each frame, and where StarMap hooks, Harmony patches and worker-thread code fit relative to the physics step

# How-To

* [Hook any frame point with Harmony patches on Program and Universe](game-loop-and-lifecycle-2.md) - Reach frame points StarMap does not expose (hidden HUD, pre-render, menu bar, key input, save loads, solver results, camera, render) with Harmony patches on KSA.Program and KSA.Universe
* [Hook the frame with StarMap lifecycle attributes](game-loop-and-lifecycle-1.md) - Run mod code at startup, each frame and at unload with StarMap lifecycle attributes, and know exactly where each one fires in KSA's frame
* [Mutate simulation state safely relative to the physics step](game-loop-and-lifecycle-3.md) - Change vehicle state only in the settled window between solver apply and dispatch, via InputEvents buffers, a PrepareFrame seam or a Vehicle.PrepareWorker prefix, and step mod motion on the engine clock
* [Run code inside the physics step on worker threads](game-loop-and-lifecycle-4.md) - Patch methods the vehicle solver runs per substep or per collision pass on worker threads, and exchange data with the main thread without races
