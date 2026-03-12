import asyncio
import websockets
import json
import struct
import re
from datetime import datetime
from pathlib import Path
from typing import Optional
import logging
from dataclasses import dataclass

logger = logging.getLogger(__name__)


@dataclass
class LogData:
    ubr_json: dict
    seq: int
    scene_id: str
    timestamp: str


@dataclass
class ContextData:
    color_data: Optional[bytes] = None
    depth_data: Optional[bytes] = None
    camera_json: Optional[dict] = None
    color_filename: str = ""
    depth_filename: str = ""
    seq: int = 0
    scene_id: str = "default"
    timestamp: str = ""
    user: str = "User1"


@dataclass
class HandData:
    hand_data: Optional[bytes] = None
    depth_data: Optional[bytes] = None
    camera_json: Optional[dict] = None
    hand_filename: str = ""
    depth_filename: str = ""
    seq: int = 0
    scene_id: str = "default"
    timestamp: str = ""
    user: str = "User1"


@dataclass
class VoiceData:
    audio_data: bytes
    filename: str
    seq: int
    scene_id: str
    timestamp: str
    user: str = "User1"


@dataclass
class ObjectData:
    glb_data: bytes
    filename: str
    seq: int
    scene_id: str
    timestamp: str
    user: str = "default"


@dataclass
class ReferentImageData:
    color_data: bytes
    camera_json: Optional[dict]
    filename: str
    seq: int
    scene_id: str
    timestamp: str
    user: str = "default"


class DataQueues:
    def __init__(self, max_size=100):
        self.log_queue = asyncio.Queue(maxsize=max_size)
        self.context_queue = asyncio.Queue(maxsize=max_size)
        self.hand_queue = asyncio.Queue(maxsize=max_size)
        self.voice_queue = asyncio.Queue(maxsize=max_size)
        self.object_queue = asyncio.Queue(maxsize=max_size)
        self.referent_image_queue = asyncio.Queue(maxsize=max_size)

    async def put_log(self, data: LogData):
            self.log_queue.put_nowait(data)

    async def put_context(self, data: ContextData):
            self.context_queue.put_nowait(data)

    async def put_hand(self, data: HandData):
            self.hand_queue.put_nowait(data)

    async def put_voice(self, data: VoiceData):
            self.voice_queue.put_nowait(data)

    async def put_object(self, data: ObjectData):
            self.object_queue.put_nowait(data)

    async def put_referent_image(self, data: ReferentImageData):
        self.referent_image_queue.put_nowait(data)


class XRDataReceiver:
    def __init__(self, save_dir="./received_data"):
        self.save_dir = Path(save_dir)
        self.save_dir.mkdir(parents=True, exist_ok=True)
        self.queues = DataQueues(max_size=100)
        self.processing_tasks = []

    async def handle_client(self, websocket):
        try:
            async for message in websocket:
                await self.process_message(message)
        except websockets.ConnectionClosed:
            pass

    async def process_message(self, message: bytes):
        header_len = struct.unpack('>I', message[0:4])[0]
        header = json.loads(message[4:4+header_len].decode('utf-8'))
        payload = message[4+header_len:]

        data_type = header.get('type')
        seq = header.get('seq', 0)
        scene_id = header.get('scene_id', 'default')

        if data_type == 'UBR':
            await self._handle_ubr(header, payload, seq, scene_id)
        elif data_type == 'Context':
            await self._handle_context(header, payload, seq, scene_id)
        elif data_type == 'Audio':
            await self._handle_voice(header, payload, seq, scene_id)
        elif data_type == 'GLB':
            await self._handle_object(header, payload, seq, scene_id)
        elif data_type == 'ARPNG':
            await self._handle_referent_image(header, payload, seq, scene_id)

    async def _handle_ubr(self, header, payload, seq, scene_id):
        ubr_json = json.loads(payload.decode('utf-8'))
        ts = ubr_json.get('Timestamp', datetime.now().strftime('%y%m%d_%H%M%S_%f')[:-3])
        log_data = LogData(ubr_json=ubr_json, seq=seq, scene_id=scene_id, timestamp=ts)
        await self.queues.put_log(log_data)

    async def _handle_context(self, header, payload, seq, scene_id):
        parts = header.get('parts', [])
        user = header.get('user', 'User1')

        is_hand = any('_hand' in p.get('filename', '') for p in parts)

        if is_hand:
            data = HandData(
                seq=seq, scene_id=scene_id, user=user,
                timestamp=datetime.now().strftime('%y%m%d_%H%M%S_%f')[:-3]
            )
            scene_dir = self.save_dir / scene_id / user / "Hand"
        else:
            data = ContextData(
                seq=seq, scene_id=scene_id, user=user,
                timestamp=datetime.now().strftime('%y%m%d_%H%M%S_%f')[:-3]
            )
            scene_dir = self.save_dir / scene_id / user / "Context"

        scene_dir.mkdir(parents=True, exist_ok=True)
        file_number = self._extract_file_number(parts) or seq

        cursor = 0
        for part in parts:
            part_data = payload[cursor:cursor+part['length']]
            cursor += part['length']

            if part['name'] == 'color':
                fname = f"color_{file_number}.png"
                if is_hand:
                    data.hand_data = part_data
                    data.hand_filename = fname
                else:
                    data.color_data = part_data
                    data.color_filename = fname
                await asyncio.to_thread(self._save_binary_file, scene_dir / fname, part_data)
            elif part['name'] == 'depth':
                fname = f"depth_{file_number}.png"
                data.depth_data = part_data
                data.depth_filename = fname
                await asyncio.to_thread(self._save_binary_file, scene_dir / fname, part_data)
            elif part['name'] == 'camera':
                data.camera_json = json.loads(part_data.decode('utf-8'))
                await asyncio.to_thread(
                    self._save_json_file,
                    scene_dir / f"camera_{file_number}.json",
                    data.camera_json
                )

        if is_hand:
            await self.queues.put_hand(data)
        else:
            await self.queues.put_context(data)

    async def _handle_voice(self, header, payload, seq, scene_id):
        parts = header.get('parts', [])
        user = header.get('user', 'User1')
        if parts:
            filename = parts[0].get('filename', f"voice_{seq}.wav")
            voice_data = VoiceData(
                audio_data=payload, filename=filename, seq=seq,
                scene_id=scene_id, user=user,
                timestamp=datetime.now().strftime('%y%m%d_%H%M%S_%f')[:-3]
            )
            await self.queues.put_voice(voice_data)

    async def _handle_object(self, header, payload, seq, scene_id):
        parts = header.get('parts', [])
        if parts:
            filename = f"{parts[0].get('filename', '').strip()}.glb"
            user = header.get('user', 'default')
            object_data = ObjectData(
                glb_data=payload, filename=filename, seq=seq,
                scene_id=scene_id, user=user,
                timestamp=datetime.now().strftime('%y%m%d_%H%M%S_%f')[:-3]
            )
            await self.queues.put_object(object_data)

    async def _handle_referent_image(self, header, payload, seq, scene_id):
        parts = header.get('parts', [])
        user = header.get('user', 'default')
        color_data = None
        camera_json = None
        filename = ""
        cursor = 0
        for part in parts:
            part_data = payload[cursor:cursor + part['length']]
            cursor += part['length']
            if part['name'] == 'color':
                color_data = part_data
                filename = part.get('filename', '').strip()
            elif part['name'] == 'camera':
                camera_json = json.loads(part_data.decode('utf-8'))
        if color_data:
            ref_data = ReferentImageData(
                color_data=color_data, camera_json=camera_json,
                filename=f"{filename}.png" if filename else f"arpng_{seq}.png",
                seq=seq, scene_id=scene_id, user=user,
                timestamp=datetime.now().strftime('%y%m%d_%H%M%S_%f')[:-3]
            )
            await self.queues.put_referent_image(ref_data)

    async def start_processing_workers(self, processor):
        self.processing_tasks = [
            asyncio.create_task(self._worker(self.queues.log_queue, processor.process_log)),
            asyncio.create_task(self._worker(self.queues.context_queue, processor.process_context)),
            asyncio.create_task(self._worker(self.queues.hand_queue, processor.process_hand)),
            asyncio.create_task(self._worker(self.queues.voice_queue, processor.process_voice)),
            asyncio.create_task(self._worker(self.queues.object_queue, processor.process_object)),
            asyncio.create_task(self._worker(self.queues.referent_image_queue, processor.process_referent_image)),
        ]

    async def _worker(self, queue, handler):
        while True:
            data = await queue.get()
            await handler(data)

    def _extract_file_number(self, parts):
        for part in parts:
            fname = part.get('filename', '')
            match = re.search(r'(\d+)\.png', fname)
            if match:
                return int(match.group(1))
        return 0

    def _save_binary_file(self, filepath: Path, data: bytes):
        filepath.parent.mkdir(parents=True, exist_ok=True)
        with open(filepath, 'wb') as f:
            f.write(data)

    def _save_json_file(self, filepath: Path, data: dict):
        filepath.parent.mkdir(parents=True, exist_ok=True)
        with open(filepath, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=2, ensure_ascii=False)

    async def start_server(self, host="0.0.0.0", port=8765, processor=None):
        if processor:
            await self.start_processing_workers(processor)
        async with websockets.serve(
            self.handle_client, host, port,
            max_size=100 * 1024 * 1024,
            ping_interval=None, ping_timeout=None, close_timeout=None
        ):
            logger.info(f"[Server] WebSocket started: ws://{host}:{port}")
            await asyncio.Future()
