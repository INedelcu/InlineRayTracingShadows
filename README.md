# Inline Ray Tracing Shadows
Sample scene that uses ray traced shadows generated by a compute shader using ray queries. 

# Effect Description
The compute shader reads the depth value from _CameraDepthTexture built-in texture. The world space position for that particular pixel is generated and from that point a number of rays are cast along the light direction with some random offsets for generating variable penumbra based on the distance from the shadow caster.
There is a simple temporal accumulation of samples which resets when parameters related to camera change.
The Rendering Path must be set to Deferred.

