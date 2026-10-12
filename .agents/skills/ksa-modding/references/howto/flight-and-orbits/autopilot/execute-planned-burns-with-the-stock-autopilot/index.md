# Game Feature

* [Execute planned burns hands-free with the stock Auto burn](overview.md) - Split a long burn into several passes, hand each computed maneuver to the stock Auto burn, detect which planned burn the flight computer just finished, and remove completed burns from the plan so a sequence runs without the player

# How-To

* [Allocate a long burn across passes from a private staging model](execute-planned-burns-with-the-stock-autopilot-2.md) - Split a total delta-v into per-pass budgets by equal burn time or equal delta-v using the rocket equation over a private SequencePerformanceList, and place each pass on the orbit the previous one produces
* [Detect the end of a stock burn with a RaisePendingAlerts prefix and postfix](execute-planned-burns-with-the-stock-autopilot-4.md) - Capture which planned burn the flight computer is ending in a prefix on RaisePendingAlerts and learn in the postfix whether stock kept it, using Harmony's __state to carry the record
* [Let stock compute the ignition timing before you arm Auto](execute-planned-burns-with-the-stock-autopilot-3.md) - Load a computed node, hold the engine off at minimum throttle until a vehicle worker has filled the BurnTarget, check that stock's alignment and ignition window fit, then give Auto back
* [Remove the last finished burn from the plan with a RaisePendingAlerts postfix](execute-planned-burns-with-the-stock-autopilot-5.md) - Delete the stale node stock leaves behind after the last planned burn completes, at the one moment where doing so cannot also delete the next burn
* [Run a multi-pass burn sequence as a state machine on stock Auto, persisted per vehicle](execute-planned-burns-with-the-stock-autopilot-1.md) - Commit one pass as an ordinary stock node, re-arm Auto after the previous pass loaded in Manual, advance on stock burn-end events and persist the execution so a reload resumes
