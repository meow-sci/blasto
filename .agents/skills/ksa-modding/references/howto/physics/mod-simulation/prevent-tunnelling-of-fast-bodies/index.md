# Game Feature

* [Detect hits for fast bodies without tunnelling](overview.md) - How a mod finds the moment of contact for small fast objects by solving closest approach, hull casts or swept shapes over each sub-step instead of testing end points

# How-To

* [Analytic closest approach t = -r.v / v.v clamped to the step](prevent-tunnelling-of-fast-bodies-1.md) - Treat the separation as linear over each sub-step, solve for the moment of closest approach, and fire proximity, contact, timed, height and ground fuses at the exact instant within the frame
* [Sweep against a bounding sphere, then cast the hull triangles](prevent-tunnelling-of-fast-bodies-2.md) - Use closest approach over the step against a craft's bounding sphere as the broad phase, then cast the step as a ray through the engine's per-triangle test, with an unknown verdict leaving the sphere standing
* [Swept circle against grown boxes, posts and a regulation cup](prevent-tunnelling-of-fast-bodies-3.md) - Replace mesh triangles with exact analytic shapes the mod defines, sweep the ball as a circle against rounded-corner boxes and post circles, and shape the felt over a cup so slow putts drop and fast ones roll over
