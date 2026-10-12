# Game Feature

* [Create, chain and read stock maneuver nodes from code](overview.md) - Turn a computed impulse into a stock maneuver node, compute set-apsis, circularize and plane-change burns, place a chain of burns on the orbits earlier burns produce, read back planned burns and draw them on a timeline

# How-To

* [Chain burns with CalculateBurnPatch so every later pass sits on the orbit the earlier ones produce](create-and-read-maneuver-nodes-4.md) - Build a detached FlightPlan per pass with CalculateBurnPatch and ComputeCompleteTrajectory, feed each pass the previous burn patch, and stop the chain when an intermediate orbit escapes, encounters or impacts
* [Compute set-apsis, circularize and plane-change impulses and plan them after the last pending burn](create-and-read-maneuver-nodes-3.md) - Closed-form impulses for raising or lowering one apse, circularizing and changing inclination, planned on the orbit left by the last pending burn so burns chain correctly
* [Create a stock maneuver node with Burn.Create and queue it through InputEvents.BurnUpdateBuffer](create-and-read-maneuver-nodes-1.md) - Build a Burn on the right patched conic and add it to the flight computer's plan by queuing a BurnUpdateData, the same way the stock Transfer Planner does, so it never races the vehicle solver
* [Read planned burns with a TryGetBurn loop and draw them on a mission timeline](create-and-read-maneuver-nodes-5.md) - Walk the flight computer's BurnPlan each frame, convert burn times to mission time, and draw them as predicted events with collision-avoiding labels
* [Schedule an impulsive burn directly on the flight computer from a solver-phase prefix](create-and-read-maneuver-nodes-2.md) - Set FlightComputer.Burn to a BurnTarget in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers, the one window where a main-thread write survives the CopyFrom copy-back
