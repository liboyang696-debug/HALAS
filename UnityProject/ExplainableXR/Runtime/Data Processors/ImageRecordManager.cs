using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.ARFoundation;
using Unity.Collections;
using UnityEngine.XR.ARSubsystems;

namespace ExplainableXR
{
    using static ExplainableXR.Utility;

    public class ImageRecordManager
    {
        private MonoBehaviour targetMono = null;
        private string dirName = null;
        private string depthDirName = null;
        private string confidenceDirName = null;
        private string metaDirName = null;
        private string handDirName = null;
        private RenderTexture renderTexture = null;
        private int imageIndex = 1;
        private Queue<string> imageIndexQueue = new Queue<string>();
        public Camera mainCam, colorCam, depthCam, handCam, depthHandCam;
        private ARCameraManager arCameraManager;
        private AROcclusionManager occlusionManager;
        private ExtendedCameraParameters cameraParams;
        public Texture2D colorTexture, depthTexture, handTexture, depthHandTexture;

        // Constructor for VR environment (virtual depth via shader)
        public ImageRecordManager(MonoBehaviour monoBehaviour, Camera snapshotCam, string rootDirName)
        {
            dirName = Path.Combine(rootDirName, "Image");
            depthDirName = Path.Combine(rootDirName, "Depth");
            handDirName = Path.Combine(rootDirName, "Hand");
            confidenceDirName = Path.Combine(rootDirName, "Confidence");
            metaDirName = Path.Combine(rootDirName, "Camera");
            var contextDirName = Path.Combine(rootDirName, "Context");

            Directory.CreateDirectory(dirName);
            Directory.CreateDirectory(depthDirName);
            Directory.CreateDirectory(handDirName);
            Directory.CreateDirectory(confidenceDirName);
            Directory.CreateDirectory(metaDirName);
            Directory.CreateDirectory(contextDirName);
            targetMono = monoBehaviour;
            imageIndex = 1;
            mainCam = snapshotCam;
            CreateColorCamera();
            CreateDepthCamera();
            CreateHandCamera();
            CreateDepthHandCamera();
            depthTexture = new Texture2D(depthCam.pixelWidth, depthCam.pixelHeight, TextureFormat.RFloat, false);
            depthTexture.filterMode = FilterMode.Point;
            colorTexture = new Texture2D(colorCam.pixelWidth, colorCam.pixelHeight, TextureFormat.ARGB32, false);
            colorTexture.filterMode = FilterMode.Point;
            handTexture = new Texture2D(handCam.pixelWidth, handCam.pixelHeight, TextureFormat.ARGB32, false);
            handTexture.filterMode = FilterMode.Point;
            depthHandTexture = new Texture2D(depthHandCam.pixelWidth, depthHandCam.pixelHeight, TextureFormat.RFloat, false);
            depthHandTexture.filterMode = FilterMode.Point;
        }

        // Constructor for AR environment (physical camera + occlusion depth)
        public ImageRecordManager(MonoBehaviour monoBehaviour, Camera snapshotCam,
            ARCameraManager arCameraManager, AROcclusionManager occlusionManager, string rootDirName)
        {
            this.arCameraManager = arCameraManager;
            this.occlusionManager = occlusionManager;
            dirName = Path.Combine(rootDirName, "Image");
            depthDirName = Path.Combine(rootDirName, "Depth");
            confidenceDirName = Path.Combine(rootDirName, "Confidence");
            metaDirName = Path.Combine(rootDirName, "Camera");
            var contextDirName = Path.Combine(rootDirName, "Context");

            Directory.CreateDirectory(dirName);
            Directory.CreateDirectory(depthDirName);
            Directory.CreateDirectory(confidenceDirName);
            Directory.CreateDirectory(metaDirName);
            Directory.CreateDirectory(contextDirName);
            targetMono = monoBehaviour;
            imageIndex = 1;
            mainCam = snapshotCam;
            CreateColorCamera();
            CreateDepthCamera();
            depthTexture = new Texture2D(depthCam.pixelWidth, depthCam.pixelHeight, TextureFormat.RGBAFloat, false);
            depthTexture.filterMode = FilterMode.Point;
            colorTexture = new Texture2D(colorCam.pixelWidth, colorCam.pixelHeight, TextureFormat.ARGB32, false);
            colorTexture.filterMode = FilterMode.Point;
        }

        // Capture AR RGB + Depth + Camera metadata (callback pattern, consistent with VR)
        public string TakePhysicalCameraScreenshot(bool recordDepth = true,
            Action<Texture2D, Texture2D, string, string> onComplete = null)
        {
            System.Threading.Interlocked.Increment(ref imageIndex);
            var fileName = $"{imageIndex}.png";
            targetMono.StartCoroutine(AR_CaptureCoroutine(fileName, recordDepth, onComplete));
            return fileName;
        }

        private IEnumerator AR_CaptureCoroutine(string fileName, bool recordDepth,
            Action<Texture2D, Texture2D, string, string> onComplete)
        {
            Texture2D newColorTex = null;
            Texture2D newDepthTex = null;
            string cameraJson = null;

            yield return targetMono.StartCoroutine(AR_ColorCamScreenshot(fileName, (tex, json) =>
            {
                newColorTex = tex;
                cameraJson = json;
            }));

            if (recordDepth)
            {
                yield return targetMono.StartCoroutine(AR_DepthCamScreenshot(fileName, tex =>
                {
                    newDepthTex = tex;
                }));
            }

            onComplete?.Invoke(newColorTex, newDepthTex, fileName, cameraJson);
        }

        private IEnumerator AR_ColorCamScreenshot(string fileName, Action<Texture2D, string> onComplete = null)
        {
            if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cameraImage))
            {
                MsgPrintError("Cannot acquire camera image");
                onComplete?.Invoke(null, null);
                yield break;
            }

            var conversionParams = new XRCpuImage.ConversionParams()
            {
                inputRect = new RectInt(0, 0, cameraImage.width, cameraImage.height),
                outputDimensions = new Vector2Int(cameraImage.width / 4, cameraImage.height / 4),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.MirrorX
            };

            // Convert XRCpuImage to Texture2D
            int dataSize = cameraImage.GetConvertedDataSize(conversionParams);
            var rawTextureData = new NativeArray<byte>(dataSize, Allocator.Temp);
            cameraImage.Convert(conversionParams, rawTextureData);

            Texture2D newColorTex = new Texture2D(
                conversionParams.outputDimensions.x, conversionParams.outputDimensions.y,
                TextureFormat.RGBA32, false);
            newColorTex.LoadRawTextureData(rawTextureData);
            newColorTex.Apply();
            rawTextureData.Dispose();

            // Save to disk
            //SaveColorTextureAsPNG(newColorTex, fileName);

            // Extract and save camera parameters
            cameraParams = mainCam.ExtractConfigsFromCamera(arCameraManager);
            SaveCameraParameters(mainCam, arCameraManager, fileName);
            string cameraJsonStr = JsonUtility.ToJson(cameraParams, true);

            cameraImage.Dispose();
            onComplete?.Invoke(newColorTex, cameraJsonStr);
        }

        private void SaveColorTextureAsPNG(XRCpuImage cpuImage, XRCpuImage.ConversionParams conversionParams, string fileName)
        {
            int dataSize = cpuImage.GetConvertedDataSize(conversionParams);
            var rawTextureData = new NativeArray<byte>(dataSize, Allocator.Temp);
            cpuImage.Convert(conversionParams, rawTextureData);

            var pngData = ImageConversion.EncodeNativeArrayToPNG(rawTextureData, GraphicsFormat.R8G8B8A8_UNorm,
                (uint)conversionParams.outputDimensions.x, (uint)conversionParams.outputDimensions.y).ToArray();

            if (pngData != null)
            {
                var dataSaveFilePath = Path.Combine(dirName, fileName);
                File.WriteAllBytes(dataSaveFilePath, pngData);
            }
            else
            {
                MsgPrintError("Failed to encode texture to PNG");
            }

            rawTextureData.Dispose();
        }

        private IEnumerator AR_DepthCamScreenshot(string fileName, Action<Texture2D> onComplete = null)
        {
            if (!occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
            {
                MsgPrintError("Cannot acquire depth image");
                onComplete?.Invoke(null);
                yield break;
            }

            if (!occlusionManager.TryAcquireEnvironmentDepthConfidenceCpuImage(out XRCpuImage depthConfidenceImage))
            {
                MsgPrintError("Cannot acquire depth confidence image");
                depthImage.Dispose();
                onComplete?.Invoke(null);
                yield break;
            }

            var conversionParams = new XRCpuImage.ConversionParams()
            {
                inputRect = new RectInt(0, 0, depthImage.width, depthImage.height),
                outputDimensions = new Vector2Int(depthImage.width, depthImage.height),
                outputFormat = TextureFormat.RFloat,
                transformation = XRCpuImage.Transformation.MirrorX
            };

            // Convert depth XRCpuImage to Texture2D
            int dataSize = depthImage.GetConvertedDataSize(conversionParams);
            var rawDepthData = new NativeArray<byte>(dataSize, Allocator.Temp);
            depthImage.Convert(conversionParams, rawDepthData);

            Texture2D newDepthTex = new Texture2D(
                conversionParams.outputDimensions.x, conversionParams.outputDimensions.y,
                TextureFormat.RFloat, false);
            newDepthTex.LoadRawTextureData(rawDepthData);
            newDepthTex.Apply();
            rawDepthData.Dispose();

            // Save to disk
            SaveDepthTextureAsPNG(newDepthTex, fileName);
            SaveDepthConfidenceTextureAsPNG(depthConfidenceImage, conversionParams, fileName);

            depthImage.Dispose();
            depthConfidenceImage.Dispose();
            onComplete?.Invoke(newDepthTex);
        }

        private void SaveDepthTextureAsPNG(XRCpuImage cpuImage, XRCpuImage.ConversionParams conversionParams, string fileName)
        {
            int dataSize = cpuImage.GetConvertedDataSize(conversionParams);
            var rawTextureData = new NativeArray<byte>(dataSize, Allocator.Temp);
            cpuImage.Convert(conversionParams, rawTextureData);

            var pngData = ImageConversion.EncodeNativeArrayToPNG(rawTextureData, GraphicsFormat.R32_SFloat,
                (uint)conversionParams.outputDimensions.x, (uint)conversionParams.outputDimensions.y).ToArray();

            if (pngData != null)
            {
                var dataSaveFilePath = Path.Combine(depthDirName, fileName);
                File.WriteAllBytes(dataSaveFilePath, pngData);

            }
            else
            {
                MsgPrintError("Failed to encode texture to PNG");
            }

            rawTextureData.Dispose();
        }

        private void SaveDepthConfidenceTextureAsPNG(XRCpuImage cpuImage,
            XRCpuImage.ConversionParams conversionParams, string fileName)
        {
            conversionParams.outputFormat = TextureFormat.R8;
            int dataSize = cpuImage.GetConvertedDataSize(conversionParams);
            var rawTextureData = new NativeArray<byte>(dataSize, Allocator.Temp);
            cpuImage.Convert(conversionParams, rawTextureData);

            var pngData = ImageConversion.EncodeNativeArrayToPNG(rawTextureData, GraphicsFormat.R8_UNorm,
                (uint)conversionParams.outputDimensions.x, (uint)conversionParams.outputDimensions.y).ToArray();

            if (pngData != null)
            {
                var dataSaveFilePath = Path.Combine(confidenceDirName, fileName);
                File.WriteAllBytes(dataSaveFilePath, pngData);
            }
            else
            {
                MsgPrintError("Failed to encode texture to PNG");
            }

            rawTextureData.Dispose();
        }

        // Capture virtual camera screenshot (color + depth), dispatched by action trigger source
        public string TakeVirtualCameraScreenshot(ActionTriggerSource actionTriggerSource,
            object actionReferent = null, Action<Texture2D, Texture2D, string, string> onComplete = null)
        {
            var actionReferentGobj = actionReferent as GameObject;
            System.Threading.Interlocked.Increment(ref imageIndex);
            var fileName = $"{imageIndex}.png";
            targetMono.StartCoroutine(CaptureCoroutine(actionTriggerSource, fileName, actionReferentGobj, onComplete));
            return fileName;
        }

        private IEnumerator CaptureCoroutine(ActionTriggerSource actionTriggerSource, string fileName,
            GameObject actionReferent, Action<Texture2D, Texture2D, string, string> onComplete)
        {
            switch (actionTriggerSource)
            {
                case ActionTriggerSource.Microphone:
                    break;

                case ActionTriggerSource.XRHand_L:
                case ActionTriggerSource.XRHand_R:
                    Texture2D newHandTex = null;
                    Texture2D newHandDepthTex = null;
                    string handCameraJson = null;

                    yield return targetMono.StartCoroutine(HandCamScreenshot(fileName, actionReferent, tex =>
                    {
                        newHandTex = tex;
                        handCameraJson = JsonUtility.ToJson(cameraParams, true);
                    }));
                    yield return targetMono.StartCoroutine(DepthHandCamScreenshot(fileName, actionReferent, tex =>
                    {
                        newHandDepthTex = tex;
                    }));
                    onComplete?.Invoke(newHandTex, newHandDepthTex, fileName, handCameraJson);
                    break;

                default: // HMD or others
                    Texture2D newColorTex = null;
                    Texture2D newDepthTex = null;
                    string colorCameraJson = null;

                    yield return targetMono.StartCoroutine(ColorCamScreenshot(fileName, actionReferent, tex =>
                    {
                        newColorTex = tex;
                        colorCameraJson = JsonUtility.ToJson(cameraParams, true);
                    }));
                    yield return targetMono.StartCoroutine(DepthCamScreenshot(fileName, actionReferent, tex =>
                    {
                        newDepthTex = tex;
                    }));
                    onComplete?.Invoke(newColorTex, newDepthTex, fileName, colorCameraJson);
                    break;
            }
        }

        private IEnumerator ColorCamScreenshot(string fileName = null,
            GameObject actionReferentGobj = null, Action<Texture2D> onComplete = null)
        {
            yield return new WaitForEndOfFrame();
            var curRT = RenderTexture.active;
            colorCam.enabled = true;
            RenderTexture.active = colorCam.targetTexture;
            actionReferentGobj?.SetActive(false);
            colorCam.Render();
            actionReferentGobj?.SetActive(true);

            Texture2D newColorTexture = new Texture2D(colorTexture.width, colorTexture.height, colorTexture.format, false);
            newColorTexture.ReadPixels(new Rect(0, 0, colorTexture.width, colorTexture.height), 0, 0, false);
            newColorTexture.Apply();

            colorCam.enabled = false;
            SaveColorTextureAsPNG(newColorTexture, fileName);
            cameraParams = colorCam.ExtractConfigsFromCamera();
            SaveCameraParameters(colorCam, fileName);
            RenderTexture.active = curRT;

            onComplete?.Invoke(newColorTexture);
        }

        private IEnumerator DepthCamScreenshot(string fileName = null,
            GameObject actionReferentGobj = null, Action<Texture2D> onComplete = null)
        {
            yield return new WaitForEndOfFrame();
            var curRT = RenderTexture.active;
            depthCam.enabled = true;
            RenderTexture.active = depthCam.targetTexture;
            actionReferentGobj?.SetActive(false);
            depthCam.Render();
            actionReferentGobj?.SetActive(true);

            Texture2D tempDepthTexture = new Texture2D(depthTexture.width, depthTexture.height, TextureFormat.RFloat, false);
            tempDepthTexture.ReadPixels(new Rect(0, 0, depthTexture.width, depthTexture.height), 0, 0, false);
            tempDepthTexture.Apply();

            depthCam.enabled = false;
            RenderTexture.active = curRT;

            Texture2D depth16Texture = ConvertRFloatToR16(tempDepthTexture);
            SaveDepthTextureAsPNG(depth16Texture, fileName);
            UnityEngine.Object.Destroy(tempDepthTexture);
            onComplete?.Invoke(depth16Texture);
        }

        private Texture2D ConvertRFloatToR16(Texture2D sourceRFloat)
        {
            int width = sourceRFloat.width;
            int height = sourceRFloat.height;
            Color[] pixelsFloat = sourceRFloat.GetPixels();
            Texture2D targetR16 = new Texture2D(width, height, TextureFormat.R16, false);
            targetR16.filterMode = FilterMode.Point;
            Color[] pixelsR16 = new Color[pixelsFloat.Length];
            for (int i = 0; i < pixelsFloat.Length; i++)
            {
                float depth = pixelsFloat[i].r;
                pixelsR16[i] = new Color(depth, 0, 0, 1);
            }
            targetR16.SetPixels(pixelsR16);
            targetR16.Apply();
            return targetR16;
        }

        private void SaveColorTextureAsPNG(Texture2D texture, string fileName)
        {
            var pngBytes = texture.EncodeToPNG();
            var dataSaveFilePath = Path.Combine(dirName, fileName);
            File.WriteAllBytes(dataSaveFilePath, pngBytes);
            MsgPrint($"Screen color snapshot saved to: {dataSaveFilePath}");
        }

        private void SaveDepthTextureAsPNG(Texture2D texture, string fileName)
        {
            var dataSaveFilePath = Path.Combine(depthDirName, fileName);
            var pngBytes = texture.EncodeToPNG();
            File.WriteAllBytes(dataSaveFilePath, pngBytes);
            MsgPrint($"Screen depth snapshot saved to: {dataSaveFilePath}");
        }

        private void ReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            using (var imageBytes = request.GetData<byte>())
            {
                var fileName = imageIndexQueue.Dequeue();
                var dataSaveFilePath = Path.Combine(dirName, fileName);
                var pngBytes = ImageConversion.EncodeNativeArrayToPNG(imageBytes, GraphicsFormat.R8G8B8A8_UNorm,
                    (uint)renderTexture.width, (uint)renderTexture.height);
                File.WriteAllBytes(dataSaveFilePath, pngBytes.ToArray());
                pngBytes.Dispose();
            }
        }

        private void CreateColorCamera()
        {
            var colorCamGobj = new GameObject("Color Camera");
            colorCam = colorCamGobj.AddComponent<Camera>();
            colorCam.CopyFrom(mainCam);
            colorCam.transform.SetParent(mainCam.transform);
            colorCam.renderingPath = RenderingPath.Forward;
            colorCam.clearFlags = CameraClearFlags.SolidColor;
            colorCam.backgroundColor = new Color(1, 1, 1, 0);
            colorCam.cullingMask = ~LayerMask.GetMask("UI");
            colorCam.farClipPlane = mainCam.farClipPlane;
            colorCam.depth = 100f;
            colorCam.targetTexture = CreateColorRenderTexture(mainCam);
            colorCam.useOcclusionCulling = false;
            colorCam.allowHDR = false;
            colorCam.allowMSAA = false;
            colorCam.enabled = false;
        }

        // Downsampled color render texture (1/4 resolution)
        private RenderTexture CreateColorRenderTexture(Camera mainCam)
        {
            RenderTexture rt = new RenderTexture(mainCam.pixelWidth / 4, mainCam.pixelHeight / 4, 24, RenderTextureFormat.ARGB32);
            rt.name = "Color RenderTexture";
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Point;
            rt.hideFlags = HideFlags.DontSave;
            rt.enableRandomWrite = true;
            rt.Create();
            return rt;
        }

        private void CreateDepthCamera()
        {
            var depthCamGobj = new GameObject("Depth Camera");
            depthCam = depthCamGobj.AddComponent<Camera>();
            depthCam.CopyFrom(mainCam);
            depthCam.transform.SetParent(mainCam.transform);
            depthCam.depthTextureMode = DepthTextureMode.Depth;
            depthCam.renderingPath = RenderingPath.Forward;
            depthCam.clearFlags = CameraClearFlags.SolidColor;
            depthCam.backgroundColor = new Color(1, 1, 1, 0);
            depthCam.cullingMask = ~LayerMask.GetMask("UI");
            depthCam.farClipPlane = mainCam.farClipPlane;
            depthCam.depth = -100f;
            depthCam.targetTexture = CreateDepthRenderTexture(mainCam);
            depthCam.SetReplacementShader(Resources.Load<Shader>("DepthShader"), "");
            depthCam.useOcclusionCulling = false;
            depthCam.allowHDR = false;
            depthCam.allowMSAA = false;
            depthCam.enabled = false;
        }

        // Downsampled depth render texture (1/4 resolution, RFloat)
        private RenderTexture CreateDepthRenderTexture(Camera mainCam)
        {
            RenderTexture rt = new RenderTexture(mainCam.pixelWidth / 4, mainCam.pixelHeight / 4, 24, RenderTextureFormat.RFloat);
            rt.name = "Depth RenderTexture";
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Point;
            rt.hideFlags = HideFlags.DontSave;
            rt.enableRandomWrite = true;
            rt.Create();
            return rt;
        }

        private void SaveCameraParameters(Camera cam, ARCameraManager arCameraManager, string fileName)
        {
            var dataSaveFilePath = Path.Combine(metaDirName, $"{fileName}.json");
            Utility.SaveCameraParameters(cam, arCameraManager, dataSaveFilePath);
        }

        private void SaveCameraParameters(Camera cam, string fileName)
        {
            var dataSaveFilePath = Path.Combine(metaDirName, $"{fileName}.json");
            Utility.SaveCameraParameters(cam, dataSaveFilePath);
        }

        private void LoadCameraParameters(Camera cam, string fileName)
        {
            var dataSaveFilePath = Path.Combine(metaDirName, $"{fileName}.json");
            Utility.LoadCameraParameters(cam, dataSaveFilePath);
        }

        private void CreateHandCamera()
        {
            var handCamGobj = new GameObject("Hand Camera");
            handCam = handCamGobj.AddComponent<Camera>();
            handCam.CopyFrom(mainCam);
            handCam.transform.SetParent(mainCam.transform);
            handCam.renderingPath = RenderingPath.Forward;
            handCam.clearFlags = CameraClearFlags.SolidColor;
            handCam.backgroundColor = new Color(1, 1, 1, 0);
            handCam.depth = 100f;
            handCam.cullingMask = LayerMask.GetMask("Hand");
            handCam.targetTexture = CreateHandRenderTexture(mainCam);
            handCam.useOcclusionCulling = false;
            handCam.allowHDR = false;
            handCam.allowMSAA = false;
            handCam.enabled = false;
        }

        private void CreateDepthHandCamera()
        {
            var depthHandCamGobj = new GameObject("Depth Hand Camera");
            depthHandCam = depthHandCamGobj.AddComponent<Camera>();
            depthHandCam.CopyFrom(mainCam);
            depthHandCam.transform.SetParent(mainCam.transform);
            depthHandCam.depthTextureMode = DepthTextureMode.Depth;
            depthHandCam.renderingPath = RenderingPath.Forward;
            depthHandCam.clearFlags = CameraClearFlags.SolidColor;
            depthHandCam.backgroundColor = new Color(1, 1, 1, 0);
            depthHandCam.cullingMask = LayerMask.GetMask("Hand");
            depthHandCam.farClipPlane = mainCam.farClipPlane;
            depthHandCam.depth = -100f;
            depthHandCam.targetTexture = CreateDepthHandRenderTexture(mainCam);
            depthHandCam.SetReplacementShader(Resources.Load<Shader>("DepthShader"), "");
            depthHandCam.useOcclusionCulling = false;
            depthHandCam.allowHDR = false;
            depthHandCam.allowMSAA = false;
            depthHandCam.enabled = false;
        }

        private RenderTexture CreateHandRenderTexture(Camera mainCam)
        {
            var rt = new RenderTexture(mainCam.pixelWidth / 4, mainCam.pixelHeight / 4, 24, RenderTextureFormat.ARGB32);
            rt.name = "Hand RenderTexture";
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Point;
            rt.hideFlags = HideFlags.DontSave;
            rt.enableRandomWrite = true;
            rt.Create();
            return rt;
        }

        private RenderTexture CreateDepthHandRenderTexture(Camera mainCam)
        {
            RenderTexture rt = new RenderTexture(mainCam.pixelWidth / 4, mainCam.pixelHeight / 4, 24, RenderTextureFormat.RFloat);
            rt.name = "Depth Hand RenderTexture";
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Point;
            rt.hideFlags = HideFlags.DontSave;
            rt.enableRandomWrite = true;
            rt.Create();
            return rt;
        }

        private IEnumerator HandCamScreenshot(string fileName = null,
            GameObject actionReferentGobj = null, Action<Texture2D> onComplete = null)
        {
            yield return new WaitForEndOfFrame();
            var curRT = RenderTexture.active;
            handCam.enabled = true;
            RenderTexture.active = handCam.targetTexture;
            actionReferentGobj?.SetActive(false);
            handCam.Render();
            actionReferentGobj?.SetActive(true);

            Texture2D newHandTexture = new Texture2D(handTexture.width, handTexture.height, handTexture.format, false);
            newHandTexture.ReadPixels(new Rect(0, 0, handTexture.width, handTexture.height), 0, 0, false);
            newHandTexture.Apply();

            handCam.enabled = false;
            cameraParams = handCam.ExtractConfigsFromCamera();
            RenderTexture.active = curRT;
            onComplete?.Invoke(newHandTexture);
        }

        private void SaveHandTextureAsPNG(Texture2D texture, string fileName)
        {
            var pngBytes = texture.EncodeToPNG();
            var dataSaveFilePath = Path.Combine(handDirName, fileName);
            File.WriteAllBytes(dataSaveFilePath, pngBytes);
        }

        private IEnumerator DepthHandCamScreenshot(string fileName = null,
            GameObject actionReferentGobj = null, Action<Texture2D> onComplete = null)
        {
            yield return new WaitForEndOfFrame();
            var curRT = RenderTexture.active;
            depthHandCam.enabled = true;
            RenderTexture.active = depthHandCam.targetTexture;
            actionReferentGobj?.SetActive(false);
            depthHandCam.Render();
            actionReferentGobj?.SetActive(true);

            Texture2D tempDepthHandTexture = new Texture2D(
                depthHandTexture.width, depthHandTexture.height, TextureFormat.RFloat, false);
            tempDepthHandTexture.ReadPixels(new Rect(0, 0, depthHandTexture.width, depthHandTexture.height), 0, 0, false);
            tempDepthHandTexture.Apply();

            depthHandCam.enabled = false;
            RenderTexture.active = curRT;

            Texture2D depthHand16Texture = ConvertRFloatToR16(tempDepthHandTexture);
            UnityEngine.Object.Destroy(tempDepthHandTexture);
            onComplete?.Invoke(depthHand16Texture);
        }

        private void SaveDepthHandTextureAsPNG(Texture2D texture, string fileName)
        {
            var dataSaveFilePath = Path.Combine(depthDirName, $"hand_{fileName}");
            var pngBytes = texture.EncodeToPNG();
            File.WriteAllBytes(dataSaveFilePath, pngBytes);
        }
    }
}
