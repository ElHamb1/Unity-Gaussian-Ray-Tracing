using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace GaussianSplatting.Editor.Utils
{
    public class ScreenshotterGUI : EditorWindow
    {
        public int imageWidth = 2880;
        public int imageHeight = 1600;
        public string storagePath = "Assets/";
        public bool includeScreenSpaceOverlays = true;
        
        public Transform targetObject = null;
        public Vector3 rotationAmount = Vector3.zero;
        
        private int camSelection = 0;


        [MenuItem("Tools/Screenshoter")]
        public static void Init()
        {
            var window = GetWindowWithRect<ScreenshotterGUI>(new Rect(50, 50, 360, 340), false,
                "Screenshotter", true);
            window.minSize = new Vector2(320, 320);
            window.maxSize = new Vector2(1500, 1500);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            GUILayout.Label("Settings", EditorStyles.boldLabel);
            imageWidth = EditorGUILayout.IntField("Image Width", imageWidth);
            imageHeight = EditorGUILayout.IntField("Image height", imageHeight);
            storagePath = EditorGUILayout.TextField("Storage path", storagePath);
            includeScreenSpaceOverlays = EditorGUILayout.Toggle(
                new GUIContent(
                    "Include Screen Overlays",
                    "Include UGUI screen-space overlays such as the replay video."),
                includeScreenSpaceOverlays);

            var cameras = Camera.allCameras;
            cameras = cameras.Prepend(SceneView.GetAllSceneCameras()[0]).ToArray();
            camSelection =
                EditorGUILayout.Popup("Source camera", this.camSelection, cameras.Select(c => c.name).ToArray());

            EditorGUILayout.Space();
            if (GUILayout.Button("Take Shot"))
            {
                Screenshotter.Capture(
                    cameras[camSelection],
                    imageWidth,
                    imageHeight,
                    storagePath,
                    includeScreenSpaceOverlays: includeScreenSpaceOverlays);
            }

            if (includeScreenSpaceOverlays && !Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "The replay video overlay exists only in Play Mode.",
                    MessageType.Info);
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.Separator();
            GUILayout.Label("Capture around target", EditorStyles.boldLabel);
            targetObject = EditorGUILayout.ObjectField("Target Transform", targetObject, typeof(Transform), true) as Transform;
            rotationAmount = EditorGUILayout.Vector3Field("Capture shot every x degree", rotationAmount);


            GUI.enabled = targetObject != null && rotationAmount != Vector3.zero;
            EditorGUILayout.Space();
            if (GUILayout.Button("Capture Shots Around Target"))
            {
                RotatingShot(cameras[camSelection]);
            }
            GUI.enabled = true;
        }

        private void RotatingShot(Camera cam)
        {
            var originalRotation = targetObject.eulerAngles;
            for (Vector3 deg = Vector3.zero; Mathf.Max(deg.x, deg.y, deg.z) < 360; deg += rotationAmount)
            {
                targetObject.eulerAngles = originalRotation + deg;
                Screenshotter.Capture(
                    cam,
                    imageWidth,
                    imageHeight,
                    storagePath,
                    silent: true,
                    includeScreenSpaceOverlays: includeScreenSpaceOverlays);
            }
            targetObject.eulerAngles = originalRotation;
        }
    }
}

public class Screenshotter : MonoBehaviour
{
    // Take a "screenshot" of a camera's Render Texture.
    public static void Capture(
        Camera cam,
        int width,
        int height,
        string outPath = "",
        bool silent = false,
        bool includeScreenSpaceOverlays = true)
    {
        if (cam == null)
            throw new ArgumentNullException(nameof(cam));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        var originalTarget = cam.targetTexture;
        var originalActive = RenderTexture.active;
        var captureTarget = new RenderTexture(width, height, 32)
        {
            name = "Screenshotter Capture"
        };
        var snapshot = new Texture2D(width, height, TextureFormat.ARGB32, false);
        byte[] bytes = null;
        bool overlayRendered = false;
        bool canRenderScreenSpaceOverlays =
            includeScreenSpaceOverlays &&
            cam.cameraType == CameraType.Game &&
            GraphicsSettings.currentRenderPipeline != null;

        void RenderScreenSpaceOverlays(
            ScriptableRenderContext context,
            Camera renderedCamera)
        {
            if (overlayRendered || renderedCamera != cam)
                return;

            overlayRendered = true;
            var commandBuffer = new CommandBuffer
            {
                name = "Screenshotter Screen-Space UI"
            };
            try
            {
                RendererList rendererList =
                    context.CreateUIOverlayRendererList(
                        renderedCamera,
                        UISubset.UIToolkit_UGUI);
                commandBuffer.SetRenderTarget(captureTarget);
                commandBuffer.SetViewport(new Rect(0f, 0f, width, height));
                commandBuffer.DrawRendererList(rendererList);
                context.ExecuteCommandBuffer(commandBuffer);
                // HDRP invokes endCameraRendering outside its profiling scopes
                // specifically so callbacks may submit additional rendering.
                context.Submit();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Screenshotter could not add screen-space overlays: " +
                    exception.Message);
            }
            finally
            {
                commandBuffer.Release();
            }
        }

        cam.targetTexture = captureTarget;
        if (canRenderScreenSpaceOverlays)
        {
            RenderPipelineManager.endCameraRendering +=
                RenderScreenSpaceOverlays;
        }

        var watch = new Stopwatch();
        try
        {
            watch.Start();

            // HDRP omits Screen Space Overlay UI for cameras with a target
            // texture. The end-camera callback above draws the same UGUI
            // renderer list after HDRP and its custom passes have finished.
            cam.Render();

            watch.Stop();
            if (!silent)
            {
                Debug.Log(
                    $"Rendering took {watch.Elapsed.TotalMilliseconds}ms");
            }

            if (includeScreenSpaceOverlays && !canRenderScreenSpaceOverlays &&
                !silent)
            {
                Debug.LogWarning(
                    "Screen-space overlays can only be added to a Game camera " +
                    "rendered by a Scriptable Render Pipeline.");
            }

            RenderTexture.active = captureTarget;
            snapshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            snapshot.Apply(false, false);
            bytes = snapshot.EncodeToPNG();
        }
        finally
        {
            if (canRenderScreenSpaceOverlays)
            {
                RenderPipelineManager.endCameraRendering -=
                    RenderScreenSpaceOverlays;
            }

            RenderTexture.active = originalActive;
            cam.targetTexture = originalTarget;
            captureTarget.Release();
            if (Application.isEditor)
            {
                DestroyImmediate(snapshot);
                DestroyImmediate(captureTarget);
            }
            else
            {
                Destroy(snapshot);
                Destroy(captureTarget);
            }
        }

        int counter = 0;
        string path;
        Directory.CreateDirectory(outPath);
        while(true)
        {
            path = System.IO.Path.Combine(outPath, $"snapshot_{counter:0000}.png" );
            if (!System.IO.File.Exists(path))
                break;
            ++counter;
        }
        System.IO.File.WriteAllBytes(path, bytes);
    }
}
