import json
import logging
from typing import Set
import websockets

logger = logging.getLogger(__name__)


class FrontendNotifier:
    def __init__(self, host="localhost", port=8766):
        self.host = host
        self.port = port
        self.clients: Set[websockets.WebSocketServerProtocol] = set()
        self.server = None
        # Replay the latest models when a browser reconnects.
        self.latest_model_messages = {}

    async def start_server(self):
        self.server = await websockets.serve(self._handle_client, self.host, self.port)
        logger.info(f"[Frontend:8766] notifier started: ws://{self.host}:{self.port}")

    async def _handle_client(self, websocket):
        logger.info(f"[Frontend:8766] connection open from {websocket.remote_address}")
        self.clients.add(websocket)
        try:
            welcome = {"type": "welcome", "message": "Connected to ExplainableXR Backend", "version": "1.0.0"}
            await websocket.send(json.dumps(welcome))
            for model_message in self.latest_model_messages.values():
                await websocket.send(json.dumps(model_message))
            async for message in websocket:
                data = json.loads(message)
                if data.get("type") == "ping":
                    await websocket.send(json.dumps({"type": "pong"}))
        except websockets.exceptions.ConnectionClosed:
            pass
        finally:
            self.clients.discard(websocket)
            logger.info(f"[Frontend:8766] connection closed from {websocket.remote_address}")

    async def notify_clients(self, message: dict):
        if message.get("type") == "new_glb":
            key = str(message.get("data_type") or "referent").lower()
            self.latest_model_messages[key] = message
            logger.info(
                "[Frontend:8766] notifying %s clients of %s GLB: %s",
                len(self.clients),
                key,
                message.get("glb_path")
            )
        elif message.get("type") == "object_ready":
            self.latest_model_messages["referent"] = message

        if not self.clients:
            return
        message_json = json.dumps(message)
        disconnected = set()
        for client in self.clients:
            try:
                await client.send(message_json)
            except websockets.exceptions.ConnectionClosed:
                disconnected.add(client)
        self.clients -= disconnected

    async def stop_server(self):
        if self.server:
            self.server.close()
            await self.server.wait_closed()
