# Game Feature

* [Upgrade a mod to a new KSA build and degrade gracefully when the API drifts](overview.md) - A repeatable procedure of refreshing the pinned binaries and decompiled corpus, diffing only what the mod touches, classifying each coupling by how it fails, fixing, recording the new build and flying it, plus runtime design that latches a single feature as degraded instead of crashing

# How-To

* [Annotate every game binding, degrade one accessor on failure and keep an impact catalog in lockstep](upgrade-a-mod-to-a-new-ksa-build-3.md) - Mark each method that binds to a KSA member with a documentary anchor attribute, latch a failing accessor as degraded instead of crashing, and keep a scope catalog and an upgrade playbook that are updated with the code
* [Review an upgrade against a coupling catalog that classifies how each coupling fails](upgrade-a-mod-to-a-new-ksa-build-2.md) - Keep a catalog of every mod-to-game coupling classified by failure mode and a skill that diffs two game builds against it and reports severity per touchpoint
* [Upgrade by locking, decompiling both builds, diffing the API surface and updating the lock last](upgrade-a-mod-to-a-new-ksa-build-1.md) - A repeatable upgrade procedure of confirm, refresh binaries and decompiled corpus, diff only what the mod touches, diff the game XML, fix, record the new build everywhere, run the chain and fly it, then push the private mirror first
