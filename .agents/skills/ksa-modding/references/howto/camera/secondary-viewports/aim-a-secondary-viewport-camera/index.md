# Game Feature

* [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](overview.md) - A secondary camera follows nothing and has its matrices built before a GUI hook runs, and the engine fills its nearby planet only for the frame camera; two ways to aim it correctly

# How-To

* [Aim from the GUI hook with LookAt, then rebuild the matrices and per-viewport shader data and fill the nearby body yourself](aim-a-secondary-viewport-camera-1.md) - KSArmory unfollows and sets Fixed, anchors the eye on the platform as that window's camera draws it, writes the pose with LookAt, then calls Camera.OnFrame, UpdateShaderData and SetCameraUbo and copies the nearby-celestial state the engine only computes for the frame camera
* [Write the pose in a FixedController.OnFrame prefix before Camera.OnFrame and synchronize the nearby body inside it](aim-a-secondary-viewport-camera-2.md) - Hot Pursuit composes the pose in the main camera's ego space, writes it before the stock Camera.OnFrame builds the matrices, repeats ClampCamera and fills the camera's nearby-celestial fields in the same prefix so no matrix rebuild is needed
