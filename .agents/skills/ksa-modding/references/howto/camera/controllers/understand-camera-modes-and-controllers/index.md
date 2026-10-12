# Game Feature

* [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](overview.md) - Reference for every camera mod covering the per-viewport Orbit, Free, Map, IVA and Fixed controllers, the viewport pass order, mode-change timing, Fixed requirements and how to pick the right camera object

# How-To

* [Gate Harmony patches on Camera so they act on the main viewport's camera only](understand-camera-modes-and-controllers-3.md) - Camera methods run for every viewport's camera, so a patch that writes the field of view or projection tests ViewportRegistry.IsMainCamera before touching the instance
* [Read the decompiled frame order, per-mode behaviour and crash inventory before writing to a camera](understand-camera-modes-and-controllers-1.md) - Where each KSA camera mode writes the pose, in what order the viewport pass runs relative to StarMap's GUI hooks, which mode changes are immediate, what Fixed requires and which inputs crash the frame loop
* [Read the scene camera with MainViewport.BaseCamera when the main camera may be the map camera](understand-camera-modes-and-controllers-2.md) - Program.GetMainCamera answers with the map camera in Map mode, so a view effect that needs the scene camera reads MainViewport.BaseCamera and takes the field of view from the camera that is actually showing
* [Run per-frame camera work from a prefix on the main viewport's GameViewport.OnFrame](understand-camera-modes-and-controllers-4.md) - A Harmony prefix and postfix on GameViewport.OnFrame filtered by reference to Program.MainViewport puts sampling, command draining and camera preparation inside the viewport pass
