# Changelog

## 0.1.0 - 2026-10-09

- Fixed Gaussian PLY import: map properties by name, default missing anatomy layers to zero, retain existing layers, and accept reordered fields, omitted normals and SH degrees 0-3. Reject unsupported formats and malformed records with specific errors. Dispose native allocations after failed imports.
- Added standalone PLY regression tests and verified complete decoding of the 562,974-vertex `flowers_1.ply` input without modifying its original field bytes.

- Added a preflight check that prevents sample/pass creation without an active HDRP asset and required support flags, and opens setup guidance/HDRP Wizard. This handles Built-in projects that installed HDRP as a dependency without assigning a pipeline asset.

- Extracted the current modified Gaussian package into a standalone HDRP UPM repository, preserving existing asset metadata.
- Declared Unity 6000.3.12f1 / HDRP 17.3 and explicit Burst, Collections, Mathematics and Core RP dependencies.
- Replaced assembly GUID references with named dependencies, removing the unresolved reference. Legacy URP adapters remain in the source but this HDRP distribution does not enable `GS_ENABLE_URP`.
- Removed an obsolete default reference to a missing sort shader from the legacy URP feature metadata; that field no longer exists in the script. Preserved its script GUID.
- Exposed the HDRP pass and its primary-light property for setup tooling.
- Added explicit camera/pass/mesh/splat setup actions, missing-resource diagnostics and pass-state logging.
- Added a self-contained synthetic sample with 512 Gaussians, glass, a receiver and a moving/deforming occluder.
- Included third-party license texts, attribution and Unity-adaptation notices.
- Added migration guidance and an explicit validation status. Fresh-project import/rendering and Windows player validation are pending.
