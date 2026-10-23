# Architecture and troubleshooting

## Rendering dependencies

`GaussianSplatHDRPPass` runs at `AfterPostProcess`. It gathers `GaussianSplatRenderer` objects and registered `Gaussian3DGRTTransmission` meshes, resolves lights, and invokes `GaussianSplat3DGRTRenderSystem`.

`GaussianSplatRenderer` loads the layered asset data using `GaussianSplatAsset`, `GaussianImageCreator` and `GaussianUtils`. Both `SplatUtilities_DeviceRadixSort.compute` and `SplatUtilities_FidelityFX.compute` include `CSDecode3DGRT`, which creates the decoded Gaussian data, SH and procedural AABB buffers.

The render system builds a private ray-tracing acceleration structure, registers procedural Gaussian and triangle mesh instances, binds camera/light state, and dispatches `Gaussian3DGRayTracing.raytrace`. Procedural and triangle hit shaders share `Gaussian3DGRTCommon.hlsl`. `Gaussian3DGRTComposite.shader` composites the output. `Gaussian3DGRTAccumulation` exposes progress and diagnostics.

Cutout, layer and semantic support live in the shared renderer and their supporting types. `GpuSorting`, shared shader includes, raster/debug shaders and both compute variants remain coupled to initialization and fallback. They must not be removed just because the backend is ray traced. Shader default references are stored in `Runtime/GaussianSplatRenderer.cs.meta`.

The `Editor` assembly contains import and authoring tools. The setup utility adds explicit scene configuration without automatically changing settings on package import. The sample has separate runtime/editor assemblies and is compiled only after importing it from Package Manager.

## Nothing appears

Open Setup and Diagnostics. Check DX12 and hardware support, active HDRP asset support, and that exactly one enabled global custom pass uses `AfterPostProcess`. Check the Game camera's culling mask and that its Custom Pass frame setting is enabled. Use a full-resolution mono Game view without TAA or MSAA.

For Gaussians, check the asset, active layers, chosen backend and shader references. Use the repair action for selected renderers if those references are missing. All shader and compute assets, plus metadata, must be present.

For meshes, check the renderer/component are enabled, the mesh uses triangles, the material contains a `Gaussian3DGRT` pass, and Exclude From 3DGRT is off. Ordinary HDRP materials are insufficient. Ray-only meshes disappear when the ray pass cannot execute; they do not turn pink as a missing raster shader would.

## Shadows and light selection

Explicitly assign point or spot lights on the custom pass. Supported enabled lights are selected in priority order, up to eight. Automatic fallback finds a directional light only. Ray-traced participants must be registered in this private acceleration structure.

For dynamic meshes use DynamicGeometry and mark geometry dirty after vertex/index changes. Preserve this setup on every enable/Play Mode entry. The transmission registry includes recovery for disabled domain/scene reload, but that workflow still belongs in release validation.

Instance masks: Gaussians `0x01`, physical meshes `0x02`, primary-only meshes `0x04`, shadow-only meshes `0x08`. Shadow-only support is retained in the generic renderer; application-specific tool masks and reconstructed-surface mesh generators are not dependencies of this package.

## Migrating the original project

The original project has not been switched to this package. Its existing local dependency and source files remain in place. To migrate later, replace its `org.nesnausk.gaussian-splatting` dependency with this package, keeping only one copy installed.

Application shaders such as reconstructed surface, tool occluder and video funnel include:

```text
Packages/org.nesnausk.gaussian-splatting/Runtime/Resources/Gaussian3DGRT/Gaussian3DGRTCommon.hlsl
```

Change those includes to:

```text
Packages/com.markus.gaussian-raytracing/Runtime/Resources/Gaussian3DGRT/Gaussian3DGRTCommon.hlsl
```

Keep GUIDs, assembly names and namespaces when migrating existing scenes. The fork is a replacement, not a second renderer that can coexist with the original package. Existing renderer functionality is preserved; package extraction only adds a public pass/light setup entry point and tooling, and changes package/reference metadata and notices.

Legacy URP files, the `Timer` helper and existing capture/reference-image utilities were retained to minimize extraction changes. URP adapters are inactive because this runtime assembly only defines `GS_ENABLE_HDRP`. Do not globally define `GS_ENABLE_URP` for this HDRP package. The old reference-image validator requires external datasets; it is not the new setup checker.
