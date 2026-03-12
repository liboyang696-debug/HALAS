using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace ExplainableXR
{
    public class EdgeUploader : MonoBehaviour
    {
        [Header("Edge Server Configuration")]
        public string wsUrl = "ws://127.0.0.1:8765/ws";

        private ClientWebSocket _ws;
        private int _seq = 0;
        private bool _isConnected = false;

        async void Awake()
        {
            await ConnectToEdgeServer();
        }

        void OnApplicationQuit()
        {
            DisconnectFromEdgeServer();
        }

        private async Task ConnectToEdgeServer()
        {
            _ws = new ClientWebSocket();
            try
            {
                await _ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                _isConnected = true;
            }
            catch (Exception)
            {
                _isConnected = false;
            }
        }

        private void DisconnectFromEdgeServer()
        {
            if (_ws == null) return;
            try
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "quit", CancellationToken.None);
            }
            catch (Exception) { }
            finally
            {
                _ws.Dispose();
                _isConnected = false;
            }
        }

        // Send UBR JSON data to edge server
        public async Task SendUBRAsync(string ubrJson, string sceneId = "default", string ubrId = null)
        {
            if (!IsReady() || string.IsNullOrEmpty(ubrJson))
                return;

            byte[] ubrBytes = Encoding.UTF8.GetBytes(ubrJson);

            var parts = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "name", "ubr" },
                    { "content_type", "application/json" },
                    { "length", ubrBytes.Length }
                }
            };

            var manifest = new Dictionary<string, object>
            {
                { "type", "UBR" },
                { "timestamp", NowSec() },
                { "scene_id", sceneId },
                { "seq", _seq++ },
                { "parts", parts }
            };

            await SendFrameAsync(manifest, ubrBytes);
        }

        // Send RGBD context images and camera data
        public async Task SendContextAsync(Texture2D colorTexture = null, Texture2D depthTexture = null,
            string cameraJson = null, string colorName = null, string depthName = null,
            string sceneId = "default", string userId = null)
        {
            if (!IsReady()) return;

            byte[] colorBytes = colorTexture != null ? colorTexture.EncodeToPNG() : Array.Empty<byte>();
            byte[] depthBytes = depthTexture != null ? depthTexture.EncodeToPNG() : Array.Empty<byte>();
            byte[] cameraBytes = string.IsNullOrEmpty(cameraJson)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(cameraJson);

            if (colorBytes.Length == 0 && depthBytes.Length == 0 && cameraBytes.Length == 0)
                return;

            var parts = new List<Dictionary<string, object>>();
            if (colorBytes.Length > 0)
                parts.Add(new Dictionary<string, object>
                {
                    { "name", "color" }, { "content_type", "image/png" },
                    { "length", colorBytes.Length }, { "filename", colorName ?? "" }
                });
            if (depthBytes.Length > 0)
                parts.Add(new Dictionary<string, object>
                {
                    { "name", "depth" }, { "content_type", "image/png" },
                    { "length", depthBytes.Length }, { "filename", depthName ?? "" }
                });
            if (cameraBytes.Length > 0)
                parts.Add(new Dictionary<string, object>
                {
                    { "name", "camera" }, { "content_type", "application/json" },
                    { "length", cameraBytes.Length }
                });

            var manifest = new Dictionary<string, object>
            {
                { "type", "Context" },
                { "timestamp", NowSec() },
                { "scene_id", sceneId },
                { "seq", _seq++ },
                { "user", userId ?? "" },
                { "parts", parts }
            };

            int totalLen = colorBytes.Length + depthBytes.Length + cameraBytes.Length;
            byte[] payload = new byte[totalLen];
            int cursor = 0;
            if (colorBytes.Length > 0)
            {
                Buffer.BlockCopy(colorBytes, 0, payload, cursor, colorBytes.Length);
                cursor += colorBytes.Length;
            }
            if (depthBytes.Length > 0)
            {
                Buffer.BlockCopy(depthBytes, 0, payload, cursor, depthBytes.Length);
                cursor += depthBytes.Length;
            }
            if (cameraBytes.Length > 0)
            {
                Buffer.BlockCopy(cameraBytes, 0, payload, cursor, cameraBytes.Length);
            }

            await SendFrameAsync(manifest, payload);
        }

        // Send audio recording data
        public async Task SendAudioAsync(byte[] audioBytes, string audioName = null,
            string sceneId = "default", string userId = null)
        {
            if (!IsReady() || audioBytes == null || audioBytes.Length == 0)
                return;

            var parts = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "name", "wav" }, { "content_type", "audio/wav" },
                    { "length", audioBytes.Length }, { "filename", audioName ?? "" }
                }
            };

            var manifest = new Dictionary<string, object>
            {
                { "type", "Audio" },
                { "timestamp", NowSec() },
                { "scene_id", sceneId },
                { "seq", _seq++ },
                { "user", userId ?? "" },
                { "parts", parts }
            };

            await SendFrameAsync(manifest, audioBytes);
        }

        // Send referent image data for AR Physical object identification
        public async Task SendReferentImageAsync(Texture2D colorTexture = null,
            string cameraJson = null, string colorName = null,
            string sceneId = "default", string userId = null)
        {
            if (!IsReady()) return;

            byte[] colorBytes = colorTexture != null ? colorTexture.EncodeToPNG() : Array.Empty<byte>();
            byte[] cameraBytes = string.IsNullOrEmpty(cameraJson)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(cameraJson);

            if (colorBytes.Length == 0 && cameraBytes.Length == 0)
                return;

            var parts = new List<Dictionary<string, object>>();
            if (colorBytes.Length > 0)
                parts.Add(new Dictionary<string, object>
                {
                    { "name", "color" }, { "content_type", "image/png" },
                    { "length", colorBytes.Length }, { "filename", colorName ?? "" }
                });
            if (cameraBytes.Length > 0)
                parts.Add(new Dictionary<string, object>
                {
                    { "name", "camera" }, { "content_type", "application/json" },
                    { "length", cameraBytes.Length }
                });

            var manifest = new Dictionary<string, object>
            {
                { "type", "ARPNG" },
                { "timestamp", NowSec() },
                { "scene_id", sceneId },
                { "seq", _seq++ },
                { "user", userId ?? "" },
                { "parts", parts }
            };

            int totalLen = colorBytes.Length + cameraBytes.Length;
            byte[] payload = new byte[totalLen];
            int cursor = 0;
            if (colorBytes.Length > 0)
            {
                Buffer.BlockCopy(colorBytes, 0, payload, cursor, colorBytes.Length);
                cursor += colorBytes.Length;
            }
            if (cameraBytes.Length > 0)
            {
                Buffer.BlockCopy(cameraBytes, 0, payload, cursor, cameraBytes.Length);
            }

            await SendFrameAsync(manifest, payload);
        }

        // Send GLB 3D model data
        public async Task SendGLBAsync(byte[] glbBytes, string glbName = null,
            string sceneId = "default", string userId = null, string glbId = null)
        {
            if (!IsReady() || glbBytes == null || glbBytes.Length == 0)
                return;

            var parts = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "name", "glb" }, { "content_type", "model/gltf-binary" },
                    { "length", glbBytes.Length }, { "filename", glbName ?? "" }
                }
            };

            var manifest = new Dictionary<string, object>
            {
                { "type", "GLB" },
                { "timestamp", NowSec() },
                { "scene_id", sceneId },
                { "seq", _seq++ },
                { "user", userId ?? "" },
                { "parts", parts }
            };

            await SendFrameAsync(manifest, glbBytes);
        }

        // Pack manifest + payload into binary frame: [4-byte header length][header JSON][payload]
        private async Task SendFrameAsync(Dictionary<string, object> manifest, byte[] payload)
        {
            byte[] headerBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest));
            int headerLen = headerBytes.Length;
            var frame = new byte[4 + headerLen + payload.Length];

            frame[0] = (byte)((headerLen >> 24) & 0xFF);
            frame[1] = (byte)((headerLen >> 16) & 0xFF);
            frame[2] = (byte)((headerLen >> 8) & 0xFF);
            frame[3] = (byte)(headerLen & 0xFF);

            Buffer.BlockCopy(headerBytes, 0, frame, 4, headerLen);
            Buffer.BlockCopy(payload, 0, frame, 4 + headerLen, payload.Length);

            await _ws.SendAsync(new ArraySegment<byte>(frame),
                WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private bool IsReady()
        {
            return _ws != null && _ws.State == WebSocketState.Open && _isConnected;
        }

        private static double NowSec()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        public bool IsConnected()
        {
            return IsReady();
        }

        public async Task ReconnectAsync()
        {
            DisconnectFromEdgeServer();
            await Task.Delay(1000);
            await ConnectToEdgeServer();
        }
    }
}
