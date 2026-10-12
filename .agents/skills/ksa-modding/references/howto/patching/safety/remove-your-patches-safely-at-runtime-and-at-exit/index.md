# Game Feature

* [Removing Harmony patches safely at runtime and at exit](overview.md) - How to remove only your own Harmony patches, from a main-thread hook, after nulling the delegates they call, and why StarMap's shutdown order means unload code usually finds nothing left to remove

# How-To

* [Lazy install and removal of one patch while it is in use](remove-your-patches-safely-at-runtime-and-at-exit-2.md) - Patch only while there is something to affect: gatOS creates a Harmony instance when the first vessel is marked, unpatches when the set empties, and does both on the main thread in the frame drain so no render code is mid-call
* [Unpatch by patch method on a shared Harmony instance](remove-your-patches-safely-at-runtime-and-at-exit-3.md) - Remove only the patch methods declared on your own class from a Harmony instance shared by many features by walking Harmony.GetAllPatchedMethods, so you never strip another feature's patches; contrast with UnpatchAll(harmony.Id), which removes every feature on the shared instance
* [UnpatchAll(ownId) from a main-thread hook after nulling delegates](remove-your-patches-safely-at-runtime-and-at-exit-1.md) - Remove only your own Harmony patches by id from a main-thread hook, null the delegates the patches call first so a stale patch cannot reach a dead mod, and know that StarMap's id-less UnpatchAll has already run by the time StarMapUnload methods are called
