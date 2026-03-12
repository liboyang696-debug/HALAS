import asyncio
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

    async def start_server(self):
        self.server = await websockets.serve(self._handle_client, self.host, self.port)
        logger.info(f"Frontend notifier started: ws://{self.host}:{self.port}")

    async def _handle_client(self, websocket):
        self.clients.add(websocket)
        try:
            welcome = {"type": "welcome", "message": "Connected to ExplainableXR Backend", "version": "1.0.0"}
            await websocket.send(json.dumps(welcome))
            async for message in websocket:
                data = json.loads(message)
                if data.get("type") == "ping":
                    await websocket.send(json.dumps({"type": "pong"}))
        except websockets.exceptions.ConnectionClosed:
            pass
        finally:
            self.clients.discard(websocket)

    async def notify_clients(self, message: dict):
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
