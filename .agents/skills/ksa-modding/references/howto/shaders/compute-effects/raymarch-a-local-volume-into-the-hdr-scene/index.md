# Game Feature

* [Raymarch a local volume or analytic sky effect into the HDR scene](overview.md) - How to draw smoke, fire, clouds and sky glows with a compute raymarch recorded before bloom, lit and occluded by KSA's own atmosphere and weather data, and priced from the game's GPU profiler

# How-To

* [Draw analytic sky effects in a compute pass: X-ray glow, curtains and debris shells chosen by the air](raymarch-a-local-volume-into-the-hdr-scene-3.md) - Choose which full-screen analytic sky effects a burst starts from the air density at it, place each layer by the body's own isothermal atmosphere, and draw them in the planet frame the game puts in its global block
* [Light a mod volume with KSA's sky and hide it behind weather clouds](raymarch-a-local-volume-into-the-hdr-scene-2.md) - Bind the viewport's atmosphere LUT set and the cloud renderer's weather images and shadow buffers to a compute pass, so a mod volume is lit by the real sky and sits correctly behind or in front of KSA's clouds
* [Raymarch a noise-eroded volume into the HDR scene: bounding sphere, jitter and a reprojected resolve](raymarch-a-local-volume-into-the-hdr-scene-1.md) - March a signed-distance torus, dome and column eroded by noise inside a bounding sphere, write it to a layer, blend the layer with reprojected history in a second pass and composite into the HDR scene before bloom
