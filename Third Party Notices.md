# Third-party notices

This distribution combines MIT-licensed code and Apache-2.0-licensed shader adaptations. It is not an MIT-only distribution. Per-file headers and the license texts under `Licenses/` describe the applicable terms.

## UnityGaussianSplatting

- Source: https://github.com/aras-p/UnityGaussianSplatting
- Copyright (c) 2023 Aras Pranckevičius.
- MIT; complete text in `Licenses/MIT-Aras.txt`.
- Provides the foundational renderer, asset representation, importer, editor tools and shared GPU routines. Local multi-layer, semantic and ray-tracing extensions are included in this fork.

## Multi-layer anatomy rendering

- Source: https://github.com/roth-hex-lab/Multi-Layer-Anatomy-GS-Unity-Rendering
- Related work: https://github.com/roth-hex-lab/Multi-Layer-Gaussian-Splatting-for-Immersive-Anatomy-Visualization
- The rendering repository identifies its Aras foundation and adds multi-layer anatomy rendering support. Its published MIT license retains the Aras copyright notice; a copy is preserved in `Licenses/MIT-Anatomy.txt` without inventing an additional copyright owner.
- No anatomy dataset or training implementation is distributed here.

## NVIDIA Vulkan Gaussian Splatting

- Source: https://github.com/nvpro-samples/vk_gaussian_splatting
- Copyright (c) 2019-2026, NVIDIA CORPORATION. All rights reserved.
- Apache License, Version 2.0; complete upstream text in `Licenses/Apache-2.0-NVIDIA.txt`.
- Applies to the locally attributed Unity adaptations in `Runtime/Resources/Gaussian3DGRT/Gaussian3DGRayTracing.raytrace`, `Gaussian3DGRTCommon.hlsl`, `Gaussian3DGRTProcedural.shader`, and `Gaussian3DGRTTransmission.shader`.
- The local files implement Unity/HDRP resource bindings, procedural/triangle integration and custom layered/semantic/lighting behavior. Adaptation notices are included in their headers. The exact historical NVIDIA source revision used for the original adaptations was not recorded in the local file headers; no unverified revision or feature-parity claim is made.
- License text retrieved from the upstream repository on 2026-10-09. The current repository root does not list a separate NOTICE file. This notice records the attribution supplied by the local sources.

## AMD FidelityFX

- `Shaders/GpuSortFidelityFX.hlsl` identifies its FidelityFX SDK basis.
- Copyright (c) 2023 Advanced Micro Devices, Inc.
- MIT; complete text in `Licenses/MIT-AMD.txt`. Existing file attribution is retained.
- Source project: https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK

## DeviceRadixSort

- Source: https://github.com/b0nes164/GPUSorting
- Copyright Thomas Smith 2/13/2023.
- MIT; complete notice remains in `Shaders/DeviceRadixSort.hlsl`, with a copy in `Licenses/MIT-DeviceRadixSort.txt`.

## Embedded TinyJSON

- The embedded parser header identifies https://github.com/pbhogan/TinyJSON, version `71fed96` (2019-05-10).
- Copyright (c) 2018 Alex Parker.
- MIT; complete notice remains in `Editor/Utils/TinyJsonParser.cs`, with a copy in `Licenses/MIT-JSON.txt`.

## Unity dependencies

HDRP, Core RP, Burst, Collections and Mathematics are resolved through Unity Package Manager rather than vendored into this repository. Their package licenses remain applicable to those separately installed dependencies. This repository does not redistribute Unity editor binaries or package-cache contents.

## Package additions and sample

New setup tools, sample code/data and documentation are provided under the contributor MIT license in `Licenses/MIT-Package-Contributors.txt`. Existing third-party copyright and license notices are retained. Models imported by end users have their own provenance and licenses; this package supplies synthetic example data only.
