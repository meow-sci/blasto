# Game Feature

* [Run an expensive solve without stalling the frame](overview.md) - Move a heavy guidance or planning solve (convex ascent, descent MPC, deorbit planner, guidance preview) off the frame and the vehicle worker, and pick it up when it finishes

# How-To

* [Build plain data on the main thread, solve on a worker, publish an immutable plan](run-an-expensive-solve-without-stalling-the-frame-1.md) - Move a multi-second guidance solve off the frame by capturing everything it needs as plain data in the vehicle step, running it on a job-owned background thread with a lock-protected result slot, and polling for an immutable plan on later steps
* [Rate-limit a solve on the wall clock and cache results per object with real and simulated time limits](run-an-expensive-solve-without-stalling-the-frame-5.md) - Keep an expensive per-frame computation affordable with a wall-clock interval that time warp cannot defeat, a long back-off for unanswerable questions, and a per-object cache invalidated by identity, simulated time and elapsed real time
* [Receding-horizon MPC on a worker with a wall-clock budget and one solver instance per thread](run-an-expensive-solve-without-stalling-the-frame-2.md) - Re-solve a guidance problem every cycle on a per-vehicle worker with immutable inputs, a single request and result slot, and a cap derived from measured wall-clock cost per iteration
* [Solve a cheaper problem synchronously inside the vehicle step with a solver time limit](run-an-expensive-solve-without-stalling-the-frame-3.md) - Run a convex solve of ten milliseconds or so on the main thread inside the per-vehicle prefix, bound each solve with a wall-clock time limit, and rate the whole search by cadence and measurement
* [Time-slice a search across frames with a C# iterator and per-step and total work budgets](run-an-expensive-solve-without-stalling-the-frame-4.md) - Write an expensive search as an IEnumerable that yields after each small unit of work, advance it a few milliseconds per frame from the vehicle step, and bound it with a per-call budget and a total work limit
