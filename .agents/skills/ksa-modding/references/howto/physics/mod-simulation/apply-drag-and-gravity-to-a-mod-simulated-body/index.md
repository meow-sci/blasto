# Game Feature

* [Apply air drag, gravity and aiming to a mod-simulated body](overview.md) - How a mod gives its own integrated ball, shell or missile the same air, water and gravity the game uses, and aims a moving target with the same model

# How-To

* [Density ratio to one reference air, ocean density below the waterline](apply-drag-and-gravity-to-a-mod-simulated-body-1.md) - Drag a body-fixed ball through the game's air and sea as a ratio to Earth sea-level air, with surface gravity from IParentBody.Mu and no gating on KSA's IsValid
* [Impact prediction in a co-rotating atmosphere](apply-drag-and-gravity-to-a-mod-simulated-body-3.md) - Integrate gravity and drag on air-relative velocity with the atmosphere rotating with the body, using a differentiable integrator so a landing prediction can be steered
* [Shell and missile drag with the same density read](apply-drag-and-gravity-to-a-mod-simulated-body-2.md) - Drag every round against a fixed reference air with the same density read, add point gravity back-dated to the sub-step plus the body's own fall, and solve a lead on a moving target by flying simulated shells through the identical model
