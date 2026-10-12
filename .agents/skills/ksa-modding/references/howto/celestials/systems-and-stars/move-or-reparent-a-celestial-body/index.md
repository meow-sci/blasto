# Game Feature

* [Move a planet or moon at runtime by swapping its orbit or changing its parent](overview.md) - Bodies are on rails, so moving one means giving it a new Orbit at a moment the orbit solver will pick it up, then repairing the Children lists, cached transforms and subtree by hand; and restoring the original orbit afterwards

# How-To

* [Priority.First prefix on ExecuteNextVehicleSolvers builds a fresh orbit from the target's state vectors](move-or-reparent-a-celestial-body-2.md) - Make a planet or moon follow another body at an offset by building a new orbit from the target's freshly applied state vectors in a first-priority prefix at the solver handoff, ordering chained updates and restoring the original orbit on unweld
* [SetOrbit plus manual Children surgery and UpdatePerFrameDataTree, with cycle guards on replay](move-or-reparent-a-celestial-body-1.md) - Swap a celestial's orbit and, when the parent differs, move it between the old and new parents' Children lists, then refresh the moved subtree so nothing samples stale frames
