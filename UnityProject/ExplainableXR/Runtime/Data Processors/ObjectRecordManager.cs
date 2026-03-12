using GLTFast.Export;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Assertions;

namespace ExplainableXR
{
    using static ExplainableXR.Utility;

    public class ObjectRecordManager
    {
        private string dirName = null;
        private int objectIndex = 1;
        private Dictionary<GameObject, string> gobjToGLBFileName = new();
        private Queue<GLBData> glbQueue;
        private object glbQueueLock;

        private ExportSettings exportSettings = new ExportSettings
        {
            Format = GltfFormat.Binary,
            FileConflictResolution = FileConflictResolution.Overwrite,
        };

        private GameObjectExportSettings gobjExportSettings = new GameObjectExportSettings
        {
            OnlyActiveInHierarchy = false,
            DisabledComponents = true
        };

        public ObjectRecordManager(string rootDirName, Queue<GLBData> glbQueue = null, object glbQueueLock = null)
        {
            dirName = Path.Combine(rootDirName, "Object");
            Directory.CreateDirectory(dirName);
            this.glbQueue = glbQueue;
            this.glbQueueLock = glbQueueLock;
            InitializeKnownObjects();
        }

        private void InitializeKnownObjects()
        {
            string[] knownObjectNames = { };
            foreach (var objName in knownObjectNames)
            {
                GameObject knownObj = GameObject.Find(objName);
                if (knownObj != null)
                    gobjToGLBFileName[knownObj] = objName;
            }
        }

        // Save a referent object as GLB; returns the file name (skips re-encoding if already known)
        public string SaveObject(GameObject targetGobj, GLBData gLBData, int actionId = 0, string timestamp = null)
        {
            if (gobjToGLBFileName.ContainsKey(targetGobj))
                return gobjToGLBFileName[targetGobj];

            var fileName = $"{targetGobj.name}.glb";
            var tempFilePath = Path.Combine(Application.temporaryCachePath, $"{System.Guid.NewGuid()}_{fileName}");
            gobjToGLBFileName[targetGobj] = fileName;
            gLBData.GLBName = targetGobj.name;

            // Use passed timestamp (consistent with UBR), generate new one if not provided
            if (string.IsNullOrEmpty(timestamp))
                gLBData.Timestamp = System.DateTime.Now.ToString("yyMMdd_HHmmss_fff");
            else
                gLBData.Timestamp = timestamp;

            _ = SaveGLBAsync(targetGobj, tempFilePath, gLBData);
            return fileName;
        }

        // Synchronous GLB export
        public async void SaveGLB(GameObject targetGobj, string dataSaveFilePath)
        {
            var export = new GameObjectExport(exportSettings, gobjExportSettings);
            var targetSceneGobjs = new GameObject[] { targetGobj };
            export.AddScene(targetSceneGobjs);

            var success = await export.SaveToFileAndDispose(dataSaveFilePath);
            Assert.IsTrue(success, "glTF data save attempt failed");
        }

        // Async GLB export: encode -> read bytes -> enqueue for upload
        public async Task SaveGLBAsync(GameObject targetGobj, string tempFilePath, GLBData gLBData)
        {
            try
            {
                var export = new GameObjectExport(exportSettings, gobjExportSettings);
                var targetSceneGobjs = new GameObject[] { targetGobj };
                export.AddScene(targetSceneGobjs);

                var success = await export.SaveToFileAndDispose(tempFilePath);
                if (!success)
                {
                    Debug.LogError("[ObjectManager] GLB save failed");
                    return;
                }

                if (!File.Exists(tempFilePath))
                {
                    Debug.LogError("[ObjectManager] Temp file not found");
                    return;
                }

                byte[] glbBytes = await Task.Run(() => File.ReadAllBytes(tempFilePath));
                gLBData.GLBBytes = glbBytes;
                File.Delete(tempFilePath);

                lock (glbQueueLock)
                {
                    glbQueue.Enqueue(gLBData);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ObjectManager] Encoding exception: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
