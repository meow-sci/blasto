# Game Feature

* [Tracking which craft carry your part, classifying them and detecting staging and loss](overview.md) - Per-frame rosters keyed by Vehicle reference, reference-keyed team tables and state-diffing detection of staging, disposal and structural loss

# How-To

* [Declare teams and alliances in a reference-keyed roster rebuilt each step](track-and-classify-craft-2.md) - Rebuild a Vehicle-keyed team table every simulated step from live installations, classify contacts by declared team and never by craft name
* [Detect staging and callouts by diffing part count, vehicle count and attached radial decouplers](track-and-classify-craft-3.md) - Infer liftoff, stage and booster separation, cutoff and max Q from counters sampled after every frame and compared with the previous sample
* [Freeze on signal loss with IsDisposed, IsControllable and StructuralLoad fractions](track-and-classify-craft-4.md) - Hold the last good values when the vehicle is destroyed or uncontrollable, and freeze before a structural breakup by reading GLoadFraction and DynamicPressureFraction
* [Sync a Vehicle-keyed roster of craft that carry your part, once per frame from two hooks](track-and-classify-craft-1.md) - Enumerate Universe.CurrentSystem.All from StarMapAfterGui or StarMapAfterOnFrame behind a latch, add craft that carry the part and drop them when disposed or the part leaves
