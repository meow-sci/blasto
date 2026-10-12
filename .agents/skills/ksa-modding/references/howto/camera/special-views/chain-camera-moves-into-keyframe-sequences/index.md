# Game Feature

* [Animate the flight camera with eased moves, keyframe sequences and saved recipes](overview.md) - Small animation classes that edit the live Orbit or Free camera each frame, queued into a sequence that plays, pauses, groups moves, blends back to the start and is stored as validated recipe JSON

# How-To

* [Queue animations into a KeyframeSequencePlayer with groups and return-to-start, and save the recipes with the scene](chain-camera-moves-into-keyframe-sequences-1.md) - Camera Controller Override plays a list of keyframes through one prefix on the Orbit and Free controllers, runs simultaneous moves as an AnimationGroup, blends back to the start pose and stores the authored sequence as validated recipe JSON restored stopped on load
* [Write eased single moves as IKeyframeAnimation classes driven from the OnFrame prefix](chain-camera-moves-into-keyframe-sequences-2.md) - Ten built-in moves share an interface, an easing helper and target-relative geometry; each captures its start state at Initialize and writes the live camera in Update, using absolute angles from the captured start to avoid drift
