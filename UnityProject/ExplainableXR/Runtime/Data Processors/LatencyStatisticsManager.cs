using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ExplainableXR
{
    /// <summary>
    /// Latency Statistics Manager - Records synchronous processing latencies
    /// Saves to text file when Unity Editor stops
    /// </summary>
    public class LatencyStatisticsManager
    {
        // Object Reference latency records
        private List<ObjectReferenceLatency> objectReferenceLatencies = new List<ObjectReferenceLatency>();
        
        // Context (RGBD) latency records
        private List<ContextLatency> contextLatencies = new List<ContextLatency>();
        
        private string saveDirectory;
        private bool isInitialized = false;
        
        public class ObjectReferenceLatency
        {
            public string UAD_ID;              // UAD identifier
            public string ObjectName;          // Object name
            public float EncodingTime;         // GLB encoding time (ms)
            public float FileIOTime;           // File read time (ms)
            public float EnqueueTime;          // GLB queue enqueue time (ms)
            public float TotalTime;            // Total processing time (ms)
            public int ByteSize;               // GLB file size (bytes)
            public string Timestamp;           // Timestamp
            public bool IsReferenceDelta;      // Is reference delta (skip encoding)
        }
        
        public class ContextLatency
        {
            public string ContextID;           // Context identifier
            public string ContextType;         // Context type (RGBD/Hand)
            public float CaptureTime;          // Screenshot capture time (ms)
            public float ProcessingTime;       // Image processing time (ms)
            public float EnqueueTime;          // Context queue enqueue time (ms)
            public float TotalTime;            // Total processing time (ms)
            public int ColorByteSize;          // Color texture size (bytes)
            public int DepthByteSize;          // Depth texture size (bytes)
            public string Timestamp;           // Timestamp
        }
        
        public static LatencyStatisticsManager Instance { get; private set; }
        
        public static void Initialize(string saveDir)
        {
            if (Instance == null)
            {
                Instance = new LatencyStatisticsManager(saveDir);
            }
        }
        
        private LatencyStatisticsManager(string saveDir)
        {
            saveDirectory = saveDir;
            isInitialized = true;
            Debug.Log($"<color=green>[LatencyStats] Initialized, save to: {saveDirectory}</color>");
        }
        
        /// <summary>
        /// Record object reference latency
        /// </summary>
        public void RecordObjectReferenceLatency(
            string uadID, 
            string objectName, 
            float encodingTime, 
            float fileIOTime, 
            float enqueueTime, 
            float totalTime, 
            int byteSize,
            bool isReferenceDelta = false)
        {
            if (!isInitialized) return;
            
            var record = new ObjectReferenceLatency
            {
                UAD_ID = uadID,
                ObjectName = objectName,
                EncodingTime = encodingTime,
                FileIOTime = fileIOTime,
                EnqueueTime = enqueueTime,
                TotalTime = totalTime,
                ByteSize = byteSize,
                Timestamp = System.DateTime.Now.ToString("HH:mm:ss.fff"),
                IsReferenceDelta = isReferenceDelta
            };
            
            lock (objectReferenceLatencies)
            {
                objectReferenceLatencies.Add(record);
            }
            
            Debug.Log($"<color=magenta>[LatencyStats] ObjectRef recorded: {uadID} | {objectName} | {totalTime:F2}ms | ReferenceDelta={isReferenceDelta}</color>");
        }
        
        /// <summary>
        /// Record context (RGBD) latency
        /// </summary>
        public void RecordContextLatency(
            string contextID,
            string contextType,
            float captureTime,
            float processingTime,
            float enqueueTime,
            float totalTime,
            int colorByteSize,
            int depthByteSize)
        {
            if (!isInitialized) return;
            
            var record = new ContextLatency
            {
                ContextID = contextID,
                ContextType = contextType,
                CaptureTime = captureTime,
                ProcessingTime = processingTime,
                EnqueueTime = enqueueTime,
                TotalTime = totalTime,
                ColorByteSize = colorByteSize,
                DepthByteSize = depthByteSize,
                Timestamp = System.DateTime.Now.ToString("HH:mm:ss.fff")
            };
            
            lock (contextLatencies)
            {
                contextLatencies.Add(record);
            }
            
            Debug.Log($"<color=magenta>[LatencyStats] Context recorded: {contextID} | {contextType} | {totalTime:F2}ms</color>");
        }
        
        /// <summary>
        /// Save all statistics to text file
        /// Call this when Unity Editor stops (OnApplicationQuit)
        /// </summary>
        public void SaveStatistics()
        {
            if (!isInitialized) return;
            
            string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filePath = Path.Combine(saveDirectory, $"LatencyStatistics_{timestamp}.txt");
            
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                writer.WriteLine("================================================================================");
                writer.WriteLine("              EXPLAINABLEXR LATENCY STATISTICS REPORT");
                writer.WriteLine("================================================================================");
                writer.WriteLine($"Generated at: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Total Object Reference records: {objectReferenceLatencies.Count}");
                writer.WriteLine($"Total Context records: {contextLatencies.Count}");
                writer.WriteLine();
                
                // ====================================================================
                // OBJECT REFERENCE STATISTICS
                // ====================================================================
                writer.WriteLine("================================================================================");
                writer.WriteLine("1. OBJECT REFERENCE (GLB) LATENCY STATISTICS");
                writer.WriteLine("================================================================================");
                writer.WriteLine();
                
                if (objectReferenceLatencies.Count > 0)
                {
                    // Separate normal encoding and reference delta
                    var normalRecords = objectReferenceLatencies.Where(r => !r.IsReferenceDelta).ToList();
                    var deltaRecords = objectReferenceLatencies.Where(r => r.IsReferenceDelta).ToList();
                    
                    writer.WriteLine($"[A] Normal Encoding (first interaction): {normalRecords.Count} records");
                    writer.WriteLine("--------------------------------------------------------------------------------");
                    
                    if (normalRecords.Count > 0)
                    {
                        var avgEncoding = normalRecords.Average(r => r.EncodingTime);
                        var avgFileIO = normalRecords.Average(r => r.FileIOTime);
                        var avgEnqueue = normalRecords.Average(r => r.EnqueueTime);
                        var avgTotal = normalRecords.Average(r => r.TotalTime);
                        var avgSize = normalRecords.Average(r => r.ByteSize);
                        
                        var maxTotal = normalRecords.Max(r => r.TotalTime);
                        var minTotal = normalRecords.Min(r => r.TotalTime);
                        
                        writer.WriteLine($"  Encoding Time    - Avg: {avgEncoding:F2}ms | Max: {normalRecords.Max(r => r.EncodingTime):F2}ms | Min: {normalRecords.Min(r => r.EncodingTime):F2}ms");
                        writer.WriteLine($"  File I/O Time    - Avg: {avgFileIO:F2}ms | Max: {normalRecords.Max(r => r.FileIOTime):F2}ms | Min: {normalRecords.Min(r => r.FileIOTime):F2}ms");
                        writer.WriteLine($"  Enqueue Time     - Avg: {avgEnqueue:F2}ms | Max: {normalRecords.Max(r => r.EnqueueTime):F2}ms | Min: {normalRecords.Min(r => r.EnqueueTime):F2}ms");
                        writer.WriteLine($"  TOTAL Time       - Avg: {avgTotal:F2}ms | Max: {maxTotal:F2}ms | Min: {minTotal:F2}ms");
                        writer.WriteLine($"  GLB Size         - Avg: {avgSize:F0}B ({avgSize/1024:F2}KB)");
                        writer.WriteLine();
                        
                        writer.WriteLine("  Detailed Records:");
                        writer.WriteLine("  --------------------------------------------------------------------------------");
                        writer.WriteLine("  UAD_ID                    | Object Name        | Encoding | FileIO | Enqueue | Total  | Size(B)");
                        writer.WriteLine("  --------------------------------------------------------------------------------");
                        foreach (var record in normalRecords)
                        {
                            writer.WriteLine($"  {record.UAD_ID,-25} | {record.ObjectName,-18} | {record.EncodingTime,8:F2} | {record.FileIOTime,6:F2} | {record.EnqueueTime,7:F2} | {record.TotalTime,6:F2} | {record.ByteSize,7}");
                        }
                        writer.WriteLine();
                    }
                    
                    writer.WriteLine($"[B] Reference Delta (repeated interaction): {deltaRecords.Count} records");
                    writer.WriteLine("--------------------------------------------------------------------------------");
                    writer.WriteLine("  (Skip encoding, only record reference)");
                    if (deltaRecords.Count > 0)
                    {
                        writer.WriteLine("  Detailed Records:");
                        writer.WriteLine("  --------------------------------------------------------------------------------");
                        writer.WriteLine("  UAD_ID                    | Object Name        | Status");
                        writer.WriteLine("  --------------------------------------------------------------------------------");
                        foreach (var record in deltaRecords)
                        {
                            writer.WriteLine($"  {record.UAD_ID,-25} | {record.ObjectName,-18} | Reference Delta (Skip)");
                        }
                    }
                    writer.WriteLine();
                }
                else
                {
                    writer.WriteLine("  No object reference records.");
                    writer.WriteLine();
                }
                
                // ====================================================================
                // CONTEXT (RGBD) STATISTICS
                // ====================================================================
                writer.WriteLine("================================================================================");
                writer.WriteLine("2. CONTEXT (RGBD) LATENCY STATISTICS");
                writer.WriteLine("================================================================================");
                writer.WriteLine();
                
                if (contextLatencies.Count > 0)
                {
                    var avgCapture = contextLatencies.Average(r => r.CaptureTime);
                    var avgProcessing = contextLatencies.Average(r => r.ProcessingTime);
                    var avgEnqueue = contextLatencies.Average(r => r.EnqueueTime);
                    var avgTotal = contextLatencies.Average(r => r.TotalTime);
                    var avgColorSize = contextLatencies.Average(r => r.ColorByteSize);
                    var avgDepthSize = contextLatencies.Average(r => r.DepthByteSize);
                    
                    var maxTotal = contextLatencies.Max(r => r.TotalTime);
                    var minTotal = contextLatencies.Min(r => r.TotalTime);
                    
                    writer.WriteLine($"  Total Context Records: {contextLatencies.Count}");
                    writer.WriteLine("  --------------------------------------------------------------------------------");
                    writer.WriteLine($"  Capture Time     - Avg: {avgCapture:F2}ms | Max: {contextLatencies.Max(r => r.CaptureTime):F2}ms | Min: {contextLatencies.Min(r => r.CaptureTime):F2}ms");
                    writer.WriteLine($"  Processing Time  - Avg: {avgProcessing:F2}ms | Max: {contextLatencies.Max(r => r.ProcessingTime):F2}ms | Min: {contextLatencies.Min(r => r.ProcessingTime):F2}ms");
                    writer.WriteLine($"  Enqueue Time     - Avg: {avgEnqueue:F2}ms | Max: {contextLatencies.Max(r => r.EnqueueTime):F2}ms | Min: {contextLatencies.Min(r => r.EnqueueTime):F2}ms");
                    writer.WriteLine($"  TOTAL Time       - Avg: {avgTotal:F2}ms | Max: {maxTotal:F2}ms | Min: {minTotal:F2}ms");
                    writer.WriteLine($"  Color Size       - Avg: {avgColorSize:F0}B ({avgColorSize/1024:F2}KB)");
                    writer.WriteLine($"  Depth Size       - Avg: {avgDepthSize:F0}B ({avgDepthSize/1024:F2}KB)");
                    writer.WriteLine();
                    
                    writer.WriteLine("  Detailed Records:");
                    writer.WriteLine("  --------------------------------------------------------------------------------");
                    writer.WriteLine("  Context_ID                | Type    | Capture | Process | Enqueue | Total  | Color(B) | Depth(B)");
                    writer.WriteLine("  --------------------------------------------------------------------------------");
                    foreach (var record in contextLatencies)
                    {
                        writer.WriteLine($"  {record.ContextID,-25} | {record.ContextType,-7} | {record.CaptureTime,7:F2} | {record.ProcessingTime,7:F2} | {record.EnqueueTime,7:F2} | {record.TotalTime,6:F2} | {record.ColorByteSize,8} | {record.DepthByteSize,8}");
                    }
                    writer.WriteLine();
                }
                else
                {
                    writer.WriteLine("  No context records.");
                    writer.WriteLine();
                }
                
                writer.WriteLine("================================================================================");
                writer.WriteLine("END OF REPORT");
                writer.WriteLine("================================================================================");
            }
            
            Debug.Log($"<color=green>[LatencyStats] ✅ Statistics saved to: {filePath}</color>");
            Debug.Log($"<color=green>[LatencyStats] Total records: ObjectRef={objectReferenceLatencies.Count}, Context={contextLatencies.Count}</color>");
        }
        
        /// <summary>
        /// Clear all statistics (for testing)
        /// </summary>
        public void ClearStatistics()
        {
            lock (objectReferenceLatencies)
            {
                objectReferenceLatencies.Clear();
            }
            lock (contextLatencies)
            {
                contextLatencies.Clear();
            }
            Debug.Log("<color=yellow>[LatencyStats] Statistics cleared</color>");
        }
    }
}
