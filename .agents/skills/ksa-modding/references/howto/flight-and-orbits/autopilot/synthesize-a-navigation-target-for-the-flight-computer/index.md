# Game Feature

* [Synthesize a NavigationTarget for an object that has no orbit](overview.md) - Build the NavigationTarget struct the navball, flight computer and Toward/Away modes read for a side target, and make the solver thread and the navball see it only while it is needed

# How-To

* [Inject a NavigationTarget into ComputeControl with a prefix, __state and a restoring finalizer](synthesize-a-navigation-target-for-the-flight-computer-1.md) - Give the stock Toward, Away and Dock-frame hold logic a navigation target the game would not build, on the solver's flight-computer copy, for one call, and put the struct back afterwards
* [Transpile NavigationTarget.Create call sites and target-presence checks for the navball](synthesize-a-navigation-target-for-the-flight-computer-2.md) - Swap the one NavigationTarget.Create call in the navball marker and navball data methods for a mod method, and rewrite Vehicle.Target null tests so a synthesized target counts as present
