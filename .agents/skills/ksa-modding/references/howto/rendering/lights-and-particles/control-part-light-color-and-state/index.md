# Game Feature

* [Control part lights from code: state, colour, intensity and beam angles](overview.md) - Switch the lights on stock light parts and vehicle lamps, recolour them, set intensity and narrow or widen spotlight cones, for one instance without affecting the shared template, and build grids and animations out of such lights

# How-To

* [Animate a light with a bounded queue of eased single-step transitions](control-part-light-color-and-state-4.md) - Move a part light from one colour and intensity to another with a chosen easing curve and queue up to 25 more steps per light, ticked by UI-frame delta time and re-capturing live start values on promotion
* [Build grids of light parts at runtime and switch each pixel through its stock light switch](control-part-light-color-and-state-3.md) - Create LightPart instances under a vessel's root, power them from batteries with Part.Connection, flip each part's PowerConsumer.LightIsActive per pixel, and recolour the whole grid with one template write per PartTemplate
* [Clone the shared light template per instance and set cone angles](control-part-light-color-and-state-2.md) - Give one light its own LightModule.TemplateData copy before editing brightness, colour or spotlight cone angles, and lower the inner angle with the outer because CreateSpotLight swaps them
* [Run disco channels on private light templates with owned actuators](control-part-light-color-and-state-5.md) - Give each LightModule a private TemplateData copy, drive colour, cone spread and keyframe actuators from a stateless time sample, and restore exactly what you replaced on stop
* [Write colour, intensity and switch state into the shared PartTemplate light component](control-part-light-color-and-state-1.md) - Find a part's LightModule.TemplateData by reflection, write Intensity and ColorRgb so the colour sticks, switch the light through PowerConsumer.LightIsActive and keep a ledger of originals for restore
