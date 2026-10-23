# Validation status

Packaging work completed on 2026-10-09 for steps 1-5: local repository extraction, metadata/dependencies, reference cleanup, licenses, setup tools and sample.

## Checks performed

- Compiled the package runtime, package Editor tools, sample runtime and sample Editor builder with Unity 6000.3.12f1's Roslyn compiler against the installed Unity/HDRP dependency assemblies. All four compilations passed without warnings after removing obsolete MSAA frame-setting calls.
- Compiler checks used the source project's installed reference assemblies, restricted to engine assemblies and the package's named dependencies. Outputs and response files are local ignored files under `.verification~`; no source-project scripts, settings or Unity scenes were changed.
- Verified package manifest/assembly references, local shader includes, Resources files, shader decode kernels, asset GUID closure, metadata presence and synthetic Gaussian binary layouts using `python Tools~/verify_package.py`.
- Preserved existing asset GUIDs and shader default references. New files have their own metadata. The original source working-tree snapshot, including uncommitted changes, is recorded by hash in `SourceSnapshot.json`.
- Removed one dangling legacy URP metadata reference for the removed `m_CSFfxSort` field. The script GUID and current renderer defaults are preserved.
- Included full license texts and retained per-file notices. Checked the NVIDIA repository root's conventional NOTICE paths; no separate NOTICE, NOTICE.md or NOTICE.txt was present when packaging.

## PLY reader regression checks

After the first external-model import exposed a fixed-stride assumption, the reader was updated to map named PLY properties into the existing 252-byte internal record. It defaults missing layers to zero and preserves source Gaussian values. Tests cover standard 248-byte input, layered 252-byte input, batch boundaries, reordered properties, missing normals, lower-order SH, unknown scalar properties and malformed/truncated/unsupported files.

The actual reader decoded all **562,974** vertices of the local `flowers_1.ply`. Every original 248-byte vertex was compared byte-for-byte with the corresponding decoded record and the added layer was checked as zero. All comparisons passed. This is decoder validation; creation of compressed Unity assets and the rendered result still require Unity-side validation.

With a .NET 8 SDK, run the same standalone tests using:

```text
dotnet run --project Tools~/PlyImportTests.csproj
dotnet run --project Tools~/PlyImportTests.csproj -- /path/to/flowers_1.ply
```

The tests compile the actual `Editor/Utils/GaussianPlyReader.cs` implementation. Unity is not required to execute these reader tests. The optional real-file check is intended for standard 248-byte, layer-free models.

## Still pending (step 6)

These checks do **not** establish that the package imports or renders correctly in a clean project. Before releasing a validated version:

1. Install from disk into a fresh Unity 6000.3.12f1 HDRP project with only the declared dependencies. Check Unity package resolution, asset import and shader compilation.
2. Import Basic Hybrid Scene and run its creation menu. Verify the saved scene/materials, shader default assignments, asset data references, and setup actions/Undo.
3. Verify the Game-view image: Gaussians, mesh-only and mixed rendering, glass transmission, opaque occlusion and moving/deforming mesh shadows under the assigned point light.
4. Import an independent Gaussian PLY. Exercise layers, semantic/cutout behavior and fallback as needed.
5. Stop and restart Play Mode, with normal reload and with domain/scene reload disabled. Confirm registrations and shadows recover.
6. Build and run a Windows DX12 player. Check shader/resource inclusion, runtime behavior and accumulation reset.
7. Exercise unsupported camera configurations and confirm useful diagnostics, including the lack of raster fallback for transmission meshes.

No clean-project import, visual capture, GPU shader test, player build, GitHub publication or release tag was performed as part of steps 1-5.
