# Basic Hybrid Scene

Import this sample through Package Manager, then use **Tools > Gaussian Ray Tracing > Samples > Create Basic Hybrid Scene**. This creates and saves a fresh scene plus three materials in a unique folder under Assets. It does not modify an existing scene; Unity offers to save modified scenes before opening the example.

The sample uses the package's setup API and does not depend on application scripts. Configure HDRP Custom Pass/Ray Tracing support and Windows DX12 in the setup window. View the result in Game view. Enter Play Mode for animation.

Contents:

- `Data/SyntheticGaussians.asset`: 512 uncompressed colored Gaussians distributed on a sphere, one active layer, zero higher-order SH.
- `Data/position.bytes`, `other.bytes`, `color.bytes`, `sh.bytes`: little-endian Gaussian data matching the package's asset format. Preserve their `.meta` files.
- `Editor/BasicHybridSceneBuilder.cs`: creates the Game camera, point light, custom pass, Gaussian object, opaque receiver, transmission glass, occluder, materials and saved scene.
- `Runtime/SampleOccluderMotion.cs`: moves and deforms a mesh, uses dynamic ray-tracing geometry and explicitly invalidates it after each vertex update.

Expected visual behavior to verify: colored Gaussian sphere behind glass, opaque receiver below, and an occluder casting a moving shadow from the assigned point light. Set the occluder component's speed to zero or disable it when comparing stochastic convergence on a stationary scene. Stop and re-enter Play Mode to check resource/registration lifecycle.

All sample data are synthetic mathematical geometry created for this package. No third-party model, anatomy data, patient data or video is included. Sample code and data are under the contributor MIT license in `Licenses/MIT-Package-Contributors.txt`.

The sample builder compiles against the target Unity/HDRP assemblies; visual verification in a clean project and player remains pending.
