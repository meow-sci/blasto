# Game Feature

* [Play scripted cinematic camera tracks with blends and easing](overview.md) - KSA has no camera track system, so mods compose keyframed shots over the player's camera in game-free code and apply them from the viewport pass, blending in and out

# How-To

* [Blend back to the start pose when a sequence ends](play-cinematic-camera-tracks-3.md) - Camera Controller Override interpolates the offset and slerps the rotation from the final keyframe back to the captured start pose, re-adding the followed object's position each frame so the blend tracks a moving vessel
* [Compose each frame's pose from keyframed tracks, manual overrides and a captured baseline](play-cinematic-camera-tracks-1.md) - gatOS evaluates Track over Override over Baseline per channel in a game-free compositor, with spline and easing playback, and applies the composed pose from a FixedController subclass inside the viewport pass
* [Release with a blend and shape tracks with named easing functions](play-cinematic-camera-tracks-4.md) - gatOS hands the camera back over camera_release_blend_s with an in-out ease that lerps position, slerps rotation and lerps the field of view to the captured baseline, and shapes tracks with linear, in, out, in-out and Bezier easings
* [Smoothstep the eye, lerp the look at equal depth and pace the ease with an averaged step](play-cinematic-camera-tracks-2.md) - KSAGolf's ChaseView.TryBlend smoothsteps the eye from the player's pose onto the chase and lerps the look at one depth, and SmoothedStep evens the simulated step for cosmetic eases that survive uneven frame times and pauses; both are headless-tested only at the pinned ref
