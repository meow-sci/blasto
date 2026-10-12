# Game Feature

* [Draw volumetric exhaust plumes and smoke trails without a real engine](overview.md) - Reuse the game's volumetric exhaust renderer for flames from a mod-owned thrust source by building VolumetricExhaustInstances and plume data yourself, edit the shared templates live and lay volumetric smoke trails behind projectiles and flares

# How-To

* [Edit the shared exhaust templates live and propagate to instances and nozzles](draw-exhaust-plumes-without-an-engine-3.md) - Change absorption, emission, noise, core-length and quality values of VolumetricExhaustTemplates at runtime, then call OnSettingsChanged on every live instance and RecomputeVolumetricExhaustLimits on every engine nozzle, with originals saved for reset
* [Lay volumetric smoke trails with one PlumeTrailEmitterState cursor per source](draw-exhaust-plumes-without-an-engine-5.md) - Draw billowing smoke behind projectiles and flares through KSA's volumetric booster-trail renderer by keeping one PlumeTrailEmitterState per source and submitting a body-fixed position every frame, reaching the renderer through one reflected private field
* [Postfix AddVolumetricExhaustInstances with an isentropic nozzle model](draw-exhaust-plumes-without-an-engine-2.md) - Place KSA's volumetric plume anywhere on a vessel and make it react to ambient pressure and throttle by computing PlumeData from nozzle parameters with an isentropic expansion and the game's own PlumeData.Compute
* [Reflect into private editor fields and replay each editor's apply sequence](draw-exhaust-plumes-without-an-engine-4.md) - Make the game's volumetric exhaust, plume trail and cloud debug editors scriptable by resolving their private objects once by reflection, writing the same fields and calling the same apply methods, with pristine capture for reset
* [Template-based plume instances with RocketNozzle.ComputePlumeData and mod-made gas numbers](draw-exhaust-plumes-without-an-engine-1.md) - Draw KSA's volumetric rocket flame from nozzles that belong to no engine by building VolumetricExhaustInstances from a Core template, shaping them with ComputePlumeData and submitting them from a postfix on Vehicle.AddVolumetricExhaustInstances
