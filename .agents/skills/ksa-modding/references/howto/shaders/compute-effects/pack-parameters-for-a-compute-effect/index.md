# Game Feature

* [Pack parameters and camera data for a compute effect](overview.md) - How to fit a compute effect's state into 128 bytes of push constants or a host-visible storage buffer, run several effects through one pipeline, and give the pass a camera pose that survives float precision and map mode

# How-To

* [Feed a compute shader thousands of items through a host-visible storage buffer](pack-parameters-for-a-compute-effect-3.md) - Allocate a persistently mapped storage buffer through the renderer's allocator, give every view and frame in flight its own slice, fill it on the CPU at record time and read it from a compute shader as a std430 array
* [Give a compute pass the camera pose without losing precision, or the wrong camera](pack-parameters-for-a-compute-effect-4.md) - Subtract the camera's double-precision ecliptic position before narrowing to float, project with the camera's row-vector matrices, and read the right camera when the player is in map mode or looking through another viewport
* [Pack a compute effect into 128 bytes: octahedral vectors and an exact flag sum](pack-parameters-for-a-compute-effect-1.md) - Fit a whole effect's state into one 128-byte push-constant block by deriving what the shader can derive, packing unit vectors into two floats and summing small integers into one exact float
* [Run several effects through one compute pipeline: a kind tag and a per-kind dispatch cap](pack-parameters-for-a-compute-effect-2.md) - Reuse one pipeline and one push block for different effects by marking a dispatch with a negative bound and a kind float, then keep only the newest few of each kind so the drawn set is stable
