# Game Feature

* [Refract and distort the rendered scene, and flash or shake it](overview.md) - Fake glass, lenses and shock rings by sampling the scene at an offset, in a fragment pass with a normal mask or in compute passes that copy the HDR scene and write it back displaced, plus whiteouts and view shake without touching the camera

# How-To

* [Flash and shake the view: a flash-only mode of a compute shader and a shock-pass shake on the sim clock](refract-and-distort-the-scene-3.md) - Whiteout the scene and shake the picture from compute passes before bloom, driven by a game-free model on the simulation clock, without writing to the camera and surviving the hidden UI
* [Refract the scene: a scene copy then a displaced write-back in two compute dispatches](refract-and-distort-the-scene-2.md) - Draw a shock ring that bends what is behind it by copying the HDR scene and writing each pixel back from the copy at an offset given by an analytic lens, with a reverse-Z depth test and a ground cut
* [Refract the scene: normal-mask UV offsets and luminance-gated dirt in a fragment pass](refract-and-distort-the-scene-1.md) - Offset the sample position by a tangent-space normal read from a packed mask texture, and gate a dirt amount by luminance so bright light lights the smudges
