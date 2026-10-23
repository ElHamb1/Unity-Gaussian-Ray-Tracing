// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace GaussianSplatting.Editor
{
    /// <summary>Explicit, undoable scene setup shared by the window and sample.</summary>
    public static class GaussianRayTracingSetup
    {
        public const string PackageId = "com.markus.gaussian-raytracing";
        public const string PackageRoot = "Packages/" + PackageId;

        public static bool CheckProjectForSceneSetup()
        {
            var hdrp = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (hdrp == null)
            {
                GaussianRayTracingSetupWindow.Open();
                EditorApplication.ExecuteMenuItem("Window/Rendering/HDRP Wizard");
                Debug.LogWarning("3DGRT needs an active HDRP pipeline asset. Installing the package does not switch a Built-in or URP project to HDRP. Use Window > Rendering > HDRP Wizard to create/assign the HDRP asset, then enable Custom Pass and Ray Tracing in Tools > Gaussian Ray Tracing > Setup and Diagnostics. Run sample creation again afterwards.");
                return false;
            }
            var settings = hdrp.currentPlatformRenderPipelineSettings;
            if (!settings.supportCustomPass || !settings.supportRayTracing)
            {
                GaussianRayTracingSetupWindow.Open();
                Debug.LogWarning("3DGRT scene creation paused: use Enable Custom Pass and Ray Tracing on active HDRP asset in Setup and Diagnostics, then run sample creation again.", hdrp);
                return false;
            }
            return true;
        }

        public static void AssignRendererResources(GaussianSplatRenderer renderer)
        {
            Undo.RecordObject(renderer, "Assign Gaussian shader resources");
            renderer.m_ShaderSplats = Load<Shader>("Shaders/RenderGaussianSplats.shader");
            renderer.m_ShaderComposite = Load<Shader>("Shaders/GaussianComposite.shader");
            renderer.m_ShaderDebugPoints = Load<Shader>("Shaders/GaussianDebugRenderPoints.shader");
            renderer.m_ShaderDebugBoxes = Load<Shader>("Shaders/GaussianDebugRenderBoxes.shader");
            renderer.m_CSSplatUtilities_deviceRadixSort = Load<ComputeShader>("Shaders/SplatUtilities_DeviceRadixSort.compute");
            renderer.m_CSSplatUtilities_fidelityFX = Load<ComputeShader>("Shaders/SplatUtilities_FidelityFX.compute");
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }

        public static T Load<T>(string relativePath) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(PackageRoot + "/" + relativePath);
            if (asset == null)
                throw new InvalidOperationException("Missing package resource: " + relativePath);
            return asset;
        }

        public static void ConfigureCamera(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            Undo.RecordObject(camera, "Configure 3DGRT camera");
            camera.allowMSAA = false;
            camera.allowDynamicResolution = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            if (hd == null) hd = Undo.AddComponent<HDAdditionalCameraData>(camera.gameObject);
            Undo.RecordObject(hd, "Configure 3DGRT camera");
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
            hd.allowDynamicResolution = false;
            hd.xrRendering = false;
            hd.customRenderingSettings = true;
            hd.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.CustomPass, true);
            hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.CustomPass] = true;
            hd.renderingPathCustomFrameSettings.msaaMode = MSAAMode.None;
            hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.MSAAMode] = true;
            EditorUtility.SetDirty(camera);
            EditorUtility.SetDirty(hd);
            PrefabUtility.RecordPrefabInstancePropertyModifications(camera);
            PrefabUtility.RecordPrefabInstancePropertyModifications(hd);
            EditorSceneManager.MarkSceneDirty(camera.gameObject.scene);
        }

        public static CustomPassVolume EnsurePass(Light primaryLight = null)
        {
            if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset hdrp) ||
                !hdrp.currentPlatformRenderPipelineSettings.supportCustomPass)
                throw new InvalidOperationException("An active HDRP asset with Custom Pass support is required. Configure it in HDRP Wizard and Setup and Diagnostics before creating the pass.");
            var scene = EditorSceneManager.GetActiveScene();
            var volume = UnityEngine.Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None)
                .FirstOrDefault(v => v.gameObject.scene == scene &&
                    v.customPasses.Any(p => p is GaussianSplatHDRPPass));
            if (volume == null)
            {
                var go = new GameObject("Gaussian Ray Tracing");
                Undo.RegisterCreatedObjectUndo(go, "Create 3DGRT pass");
                volume = Undo.AddComponent<CustomPassVolume>(go);
            }
            Undo.RecordObject(volume, "Configure 3DGRT pass");
            volume.enabled = true;
            volume.isGlobal = true;
            volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
            var pass = volume.customPasses.OfType<GaussianSplatHDRPPass>().FirstOrDefault();
            if (pass == null) pass = volume.AddPassOfType<GaussianSplatHDRPPass>();
            pass.name = "Gaussian Ray Tracing";
            pass.enabled = true;
            if (primaryLight != null) pass.PrimaryLight = primaryLight;
            EditorUtility.SetDirty(volume);
            PrefabUtility.RecordPrefabInstancePropertyModifications(volume);
            EditorSceneManager.MarkSceneDirty(volume.gameObject.scene);
            return volume;
        }

        public static GaussianSplatRenderer CreateRenderer(GaussianSplatAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            var go = new GameObject("Gaussian Splats");
            go.SetActive(false);
            Undo.RegisterCreatedObjectUndo(go, "Create Gaussian renderer");
            var renderer = Undo.AddComponent<GaussianSplatRenderer>(go);
            AssignRendererResources(renderer);
            renderer.m_Asset = asset;
            renderer.m_RenderBackend = GaussianSplatRenderer.RenderBackend.RayTracing3DGRT;
            renderer.m_LayerActivationState = asset.layerInfo.Keys
                .Select(layer => new Unity.Mathematics.int2(layer, 1)).ToList();
            go.SetActive(true);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return renderer;
        }

        public static void RegisterMesh(GameObject go, Material material)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            var filter = go.GetComponent<MeshFilter>();
            if (renderer == null || filter == null || filter.sharedMesh == null)
                throw new ArgumentException("Select an object with a MeshFilter, mesh and MeshRenderer.");
            if (material == null || material.FindPass("Gaussian3DGRT") < 0)
                throw new ArgumentException("The material must contain a Gaussian3DGRT pass.");
            Undo.RecordObject(renderer, "Assign 3DGRT mesh material");
            renderer.sharedMaterials = Enumerable.Repeat(material, filter.sharedMesh.subMeshCount).ToArray();
            renderer.rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.DynamicGeometry;
            var transmission = go.GetComponent<Gaussian3DGRTTransmission>();
            if (transmission == null) transmission = Undo.AddComponent<Gaussian3DGRTTransmission>(go);
            Undo.RecordObject(transmission, "Enable 3DGRT mesh");
            transmission.enabled = true;
            transmission.MarkGeometryDirty();
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(transmission);
            EditorSceneManager.MarkSceneDirty(go.scene);
        }
    }

    public sealed class GaussianRayTracingSetupWindow : EditorWindow
    {
        Camera m_Camera;
        Light m_Light;
        GaussianSplatAsset m_Asset;
        Material m_Material;
        Vector2 m_Scroll;

        [MenuItem("Tools/Gaussian Ray Tracing/Setup and Diagnostics")]
        public static void Open() => GetWindow<GaussianRayTracingSetupWindow>("Gaussian Ray Tracing");

        void OnEnable()
        {
            minSize = new Vector2(440, 530);
            m_Camera = Camera.main;
        }

        void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.LabelField("Gaussian Ray Tracing for HDRP", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Initial target: Unity 6000.3.12f1, HDRP 17.3, Windows DX12 and hardware ray tracing. Use Game view at full resolution. Setup changes happen only when you press a button.", MessageType.Info);
            var hdrp = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            Status(hdrp != null, "An HDRP asset is active.", "Select an HDRP pipeline asset in Graphics/Quality settings first.");
            if (hdrp == null)
            {
                EditorGUILayout.HelpBox("HDRP is installed but no HDRP pipeline asset is active. In a Built-in project, use HDRP Wizard to create and assign the asset. Also check Quality settings for a pipeline override.", MessageType.Error);
                if (GUILayout.Button("Open HDRP Wizard to configure the project"))
                    EditorApplication.ExecuteMenuItem("Window/Rendering/HDRP Wizard");
            }
            Status(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12,
                "Editor uses Direct3D 12.", "Editor is not using Direct3D 12. Configure Windows DX12 below, then restart Unity.");
            Status(SystemInfo.supportsRayTracing, "Hardware ray tracing is available.",
                "Unity currently reports no ray-tracing support. Check GPU/driver, DX12 and HDRP settings.");
            if (hdrp != null)
            {
                var settings = hdrp.currentPlatformRenderPipelineSettings;
                Status(settings.supportCustomPass && settings.supportRayTracing,
                    "HDRP supports custom passes and ray tracing.", "Enable Custom Pass and Ray Tracing on the active HDRP asset.");
            }
            bool resources = Resources.Load<RayTracingShader>("Gaussian3DGRT/Gaussian3DGRayTracing") != null &&
                Resources.Load<Shader>("Gaussian3DGRT/Gaussian3DGRTProcedural") != null &&
                Resources.Load<Shader>("Gaussian3DGRT/Gaussian3DGRTComposite") != null &&
                Resources.Load<Shader>("Gaussian3DGRT/Gaussian3DGRTTransmission") != null;
            Status(resources, "Ray-tracing resources are present.", "Package ray-tracing resources are missing; reimport the package and inspect shader errors.");
            string[] sharedShaders = { "RenderGaussianSplats.shader", "GaussianComposite.shader",
                "GaussianDebugRenderPoints.shader", "GaussianDebugRenderBoxes.shader" };
            bool sharedResources = sharedShaders.All(file => AssetDatabase.LoadAssetAtPath<Shader>(
                GaussianRayTracingSetup.PackageRoot + "/Shaders/" + file) != null);
            foreach (string file in new[] { "SplatUtilities_DeviceRadixSort.compute", "SplatUtilities_FidelityFX.compute" })
            {
                var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(GaussianRayTracingSetup.PackageRoot + "/Shaders/" + file);
                sharedResources &= compute != null && compute.HasKernel("CSDecode3DGRT");
            }
            Status(sharedResources, "Shared renderer shaders and Gaussian decode kernels are present.",
                "Shared shaders or CSDecode3DGRT are missing. These are required even when rendering with ray tracing.");
            bool duplicate = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Any(p => p.name == "org.nesnausk.gaussian-splatting");
            if (duplicate) EditorGUILayout.HelpBox("Remove the upstream Gaussian Splatting package. This fork retains its assembly names and GUIDs and cannot coexist with it.", MessageType.Error);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                using (new EditorGUI.DisabledScope(Application.platform != RuntimePlatform.WindowsEditor))
                {
                    if (GUILayout.Button("Configure Windows DX12 (restart Unity afterwards)"))
                    {
                        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
                        ShowNotification(new GUIContent("DX12 configured. Restart Unity to apply."));
                    }
                }
                using (new EditorGUI.DisabledScope(hdrp == null))
                {
                    if (GUILayout.Button("Enable Custom Pass and Ray Tracing on active HDRP asset"))
                    {
                        Undo.RecordObject(hdrp, "Enable HDRP ray tracing");
                        var settings = hdrp.currentPlatformRenderPipelineSettings;
                        settings.supportCustomPass = true;
                        settings.supportRayTracing = true;
                        hdrp.currentPlatformRenderPipelineSettings = settings;
                        EditorUtility.SetDirty(hdrp);
                        AssetDatabase.SaveAssets();
                    }
                }
                EditorGUILayout.Space();
                m_Camera = (Camera)EditorGUILayout.ObjectField("Game camera", m_Camera, typeof(Camera), true);
                if (m_Camera != null)
                {
                    var hd = m_Camera.GetComponent<HDAdditionalCameraData>();
                    bool cameraReady = hd != null && !m_Camera.allowMSAA && !m_Camera.allowDynamicResolution &&
                        !hd.allowDynamicResolution && !hd.xrRendering &&
                        hd.antialiasing != HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing &&
                        m_Camera.stereoTargetEye == StereoTargetEyeMask.None && hd.customRenderingSettings &&
                        hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.CustomPass] &&
                        hd.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.CustomPass);
                    Status(cameraReady, "Camera has explicit compatible settings.", "Configure this camera for mono, full resolution, no MSAA/TAA, and Custom Pass enabled.");
                }
                using (new EditorGUI.DisabledScope(m_Camera == null))
                    if (GUILayout.Button("Configure camera")) GaussianRayTracingSetup.ConfigureCamera(m_Camera);
                m_Light = (Light)EditorGUILayout.ObjectField("Primary light", m_Light, typeof(Light), true);
                if (m_Light != null && m_Light.type != LightType.Directional && m_Light.type != LightType.Point && m_Light.type != LightType.Spot)
                    EditorGUILayout.HelpBox("Choose a directional, point or spot light.", MessageType.Warning);
                if (GUILayout.Button("Create / configure global 3DGRT pass"))
                {
                    if (GaussianRayTracingSetup.CheckProjectForSceneSetup())
                        Selection.activeGameObject = GaussianRayTracingSetup.EnsurePass(m_Light).gameObject;
                }

                EditorGUILayout.Space();
                m_Asset = (GaussianSplatAsset)EditorGUILayout.ObjectField("Gaussian asset", m_Asset, typeof(GaussianSplatAsset), false);
                if (GUILayout.Button("Open Gaussian PLY importer")) GaussianSplatAssetCreator.Init();
                using (new EditorGUI.DisabledScope(m_Asset == null))
                    if (GUILayout.Button("Create Gaussian renderer"))
                        Selection.activeGameObject = GaussianRayTracingSetup.CreateRenderer(m_Asset).gameObject;
                m_Material = (Material)EditorGUILayout.ObjectField("Mesh material", m_Material, typeof(Material), false);
                EditorGUILayout.HelpBox("Registering meshes replaces all material slots on the selected mesh objects. If no material is assigned, a new transmission material is saved under Assets.", MessageType.Info);
                if (GUILayout.Button("Register selected meshes and assign material")) RegisterSelected();
                if (GUILayout.Button("Repair selected Gaussian renderer's shader references"))
                    foreach (var go in Selection.gameObjects)
                    {
                        var renderer = go.GetComponent<GaussianSplatRenderer>();
                        if (renderer != null) GaussianRayTracingSetup.AssignRendererResources(renderer);
                    }
            }

            EditorGUILayout.Space();
            var volumes = UnityEngine.Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None);
            int passCount = volumes.Where(v => v.enabled && v.isGlobal && v.injectionPoint == CustomPassInjectionPoint.AfterPostProcess)
                .Sum(v => v.customPasses.Count(p => p is GaussianSplatHDRPPass && p.enabled));
            Status(passCount == 1, "One active global 3DGRT pass is present.",
                passCount == 0 ? "No active global 3DGRT pass at AfterPostProcess." : "Multiple global 3DGRT passes found; disable duplicates.");
            if (GUILayout.Button("Log last rendered pass state")) LogState();
            EditorGUILayout.HelpBox("Pass diagnostics describe the last Game camera render. They do not replace shader compilation, a fresh-project import or player testing.", MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        void RegisterSelected()
        {
            var objects = Selection.gameObjects.Where(go => go.scene.IsValid() &&
                go.GetComponent<MeshRenderer>() != null && go.GetComponent<MeshFilter>()?.sharedMesh != null).ToArray();
            if (objects.Length == 0)
            {
                ShowNotification(new GUIContent("Select scene objects with a MeshFilter and MeshRenderer."));
                return;
            }
            if (m_Material == null)
            {
                m_Material = new Material(GaussianRayTracingSetup.Load<Shader>("Runtime/Resources/Gaussian3DGRT/Gaussian3DGRTTransmission.shader"));
                AssetDatabase.CreateAsset(m_Material, AssetDatabase.GenerateUniqueAssetPath("Assets/3DGRT Transmission.mat"));
                AssetDatabase.SaveAssets();
            }
            if (m_Material.FindPass("Gaussian3DGRT") < 0)
            {
                ShowNotification(new GUIContent("Choose a material with a Gaussian3DGRT shader pass."));
                return;
            }
            foreach (var go in objects) GaussianRayTracingSetup.RegisterMesh(go, m_Material);
        }

        static void Status(bool ok, string success, string failure) =>
            EditorGUILayout.HelpBox(ok ? success : failure, ok ? MessageType.Info : MessageType.Warning);

        [MenuItem("Tools/Gaussian Ray Tracing/Log Pass State")]
        static void LogState() => Debug.Log(
            $"3DGRT last pass: ran={Gaussian3DGRTAccumulation.LastPassRan}, " +
            $"splats={Gaussian3DGRTAccumulation.LastSplatCount}, meshes={Gaussian3DGRTAccumulation.LastTransmissionMeshCount}, " +
            $"samples={Gaussian3DGRTAccumulation.SampleId}/{Gaussian3DGRTAccumulation.SampleCount}, " +
            $"frame={Gaussian3DGRTAccumulation.RenderFrame}");
    }
}
