# Game Feature

* [Export live game state to background threads with immutable snapshots and a fixed sample clock](overview.md) - Keep transport threads away from KSA objects by building one immutable snapshot on the game thread and publishing it with a single volatile swap, sampled at a drift-free fixed cadence

# How-To

* [Plain-struct snapshot sampled after the frame, drawn next frame](export-game-state-to-other-threads-2.md) - When every consumer is a main-thread UI, copy kinematics into a plain snapshot object from StarMapAfterOnFrame and let the next frame draw it, with no locks and one frame of latency
* [Sampler builds an immutable snapshot, volatile swap, TaskCompletionSource wake](export-game-state-to-other-threads-1.md) - Publish one immutable SimSnapshot from the game thread with a single volatile reference swap so any number of transport threads read lock-free and async waiters wake on publish, sampled by a drift-free clock
