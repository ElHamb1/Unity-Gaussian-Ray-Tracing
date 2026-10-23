// SPDX-License-Identifier: MIT
using System;
using System.IO;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace GaussianSplatting.Samples.Editor
{
    public static class BasicHybridSceneBuilder
    {
        const string AssetGuid = "c182f369a4014188aab4f45d74e006c9";

        [MenuItem("Tools/Gaussian Ray Tracing/Samples/Create Basic Hybrid Scene")]
        public static void Create()
        {
            if (!GaussianRayTracingSetup.CheckProjectForSceneSetup()) return;
            var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(AssetDatabase.GUIDToAssetPath(AssetGuid));
            if (asset == null) throw new InvalidOperationException("Import the complete Basic Hybrid Scene sample from Package Manager first.");
            // Check resources before opening another scene or writing output.
            var shader = GaussianRayTracingSetup.Load<Shader>("Runtime/Resources/Gaussian3DGRT/Gaussian3DGRTTransmission.shader");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string output = AssetDatabase.GenerateUniqueAssetPath("Assets/3DGRT Basic Hybrid Scene");
            Directory.CreateDirectory(output);
            AssetDatabase.Refresh();

            var cameraGo = new GameObject("Main Camera", typeof(Camera));
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.GetComponent<Camera>();
            camera.transform.position = new Vector3(4.2f, 3.2f, -6.5f);
            camera.transform.LookAt(new Vector3(0, 0.9f, 0));
            camera.fieldOfView = 43;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100;
            GaussianRayTracingSetup.ConfigureCamera(camera);
            var cameraData = cameraGo.GetComponent<HDAdditionalCameraData>();
            cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            cameraData.backgroundColorHDR = new Color(0.025f, 0.035f, 0.05f, 1);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.08f, 0.08f, 0.08f);
            var lightGo = new GameObject("Point Light", typeof(Light));
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Point;
            light.transform.position = new Vector3(-0.8f, 4f, -1.5f);
            light.range = 20;
            light.color = new Color(1, 0.95f, 0.88f);
            light.shadows = LightShadows.Hard;
            lightGo.AddComponent<HDAdditionalLightData>();
            light.lightUnit = LightUnit.Candela;
            light.intensity = 900;
            GaussianRayTracingSetup.EnsurePass(light);

            var opaque = Material(shader, output, "Opaque Receiver", new Color(0.65f, 0.7f, 0.76f), 0, 0.6f);
            var glass = Material(shader, output, "Glass", new Color(0.8f, 0.96f, 1f), 0.96f, 0.05f);
            var occluderMaterial = Material(shader, output, "Occluder", new Color(0.8f, 0.23f, 0.07f), 0, 0.35f);
            Mesh("Opaque receiver", new Vector3(0, -0.15f, 0), new Vector3(6, 0.3f, 5), opaque);
            Mesh("Transmission glass", new Vector3(-0.85f, 0.95f, -0.8f), new Vector3(1.4f, 1.9f, 0.2f), glass);
            var occluder = Mesh("Moving and deforming occluder", new Vector3(1.1f, 1.15f, 0.3f), new Vector3(0.65f, 0.3f, 0.7f), occluderMaterial);
            occluder.AddComponent<SampleOccluderMotion>();
            var renderer = GaussianRayTracingSetup.CreateRenderer(asset);
            renderer.name = "Synthetic Gaussian Sphere (512 splats)";
            renderer.transform.position = new Vector3(-0.8f, 0.9f, 0.35f);
            renderer.m_SHOrder = 0;

            AssetDatabase.SaveAssets();
            string scenePath = output + "/BasicHybridScene.unity";
            if (!EditorSceneManager.SaveScene(scene, scenePath))
                throw new IOException("Could not save sample scene to " + scenePath);
            Selection.activeGameObject = renderer.gameObject;
            GaussianRayTracingSetupWindow.Open();
            Debug.Log("Created " + scenePath + ". Configure HDRP/DX12 using Setup and Diagnostics, then open Game view and enter Play Mode. The point light should cast a moving mesh shadow onto the receiver.");
        }

        [MenuItem("Tools/Gaussian Ray Tracing/Samples/Create Basic Hybrid Scene", true)]
        static bool CanCreate() => !EditorApplication.isPlayingOrWillChangePlaymode;

        static Material Material(Shader shader, string folder, string name, Color color, float transmission, float roughness)
        {
            var material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Transmission", transmission);
            material.SetFloat("_Roughness", roughness);
            material.SetFloat("_Ior", 1.5f);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        static GameObject Mesh(string name, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = scale;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            GaussianRayTracingSetup.RegisterMesh(go, material);
            return go;
        }
    }
}
