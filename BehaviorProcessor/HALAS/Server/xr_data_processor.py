import asyncio
import json
import os
import sys
import re
import cv2
import numpy as np
from pathlib import Path
from typing import Optional
import logging
from collections import deque
import tempfile
from concurrent.futures import ProcessPoolExecutor
import multiprocessing
from ultralytics import YOLO

project_root = Path(__file__).resolve().parent.parent.parent
llm_dir = str(Path(__file__).resolve().parent.parent / "LLM")
sys.path.insert(0, str(project_root))
sys.path.insert(0, llm_dir)

import config
from llm_caller import LLMCaller
from point_cloud_worker import reconstruct_point_cloud_task

logger = logging.getLogger(__name__)


class XRDataProcessor:
    def __init__(self, output_dir="./output", webapp_root=None, frontend_notifier=None,
                 num_workers=2, reference_dir=None):
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)
        for subdir in ["logs", "context", "hand", "voice", "glb"]:
            (self.output_dir / subdir).mkdir(exist_ok=True)

        self.reference_dir = Path(reference_dir) if reference_dir else Path(__file__).parent.parent / "Reference"
        self.reference_dir.mkdir(parents=True, exist_ok=True)
        self.pending_logs = deque()
        self.ar_referent_results = {}
        self.yolo_model = YOLO(config.YOLO_MODEL_PATH)
        logger.info(f"[YOLO] Loaded model: {config.YOLO_MODEL_PATH}")

        self.webapp_reference_dir = Path(webapp_root) / "obj" / "reference" if webapp_root else None
        if self.webapp_reference_dir:
            self.webapp_reference_dir.mkdir(parents=True, exist_ok=True)

        self.log_queue = deque(maxlen=1000)
        self.enable_llm = True
        self.llm_client = None
        self.whisper_model = None
        self.cached_context = None
        self.cached_action_description = "This is a test description"
        self.frontend_notifier = frontend_notifier
        self.event_loop = asyncio.get_event_loop()
        self.continuous_actions = {}

        if webapp_root:
            self.webapp_context_dir = Path(webapp_root) / "obj" / "realtime" / "context"
            self.webapp_hand_dir = Path(webapp_root) / "obj" / "realtime" / "hand"
            self.webapp_context_dir.mkdir(parents=True, exist_ok=True)
            self.webapp_hand_dir.mkdir(parents=True, exist_ok=True)
        else:
            self.webapp_context_dir = None
            self.webapp_hand_dir = None

        self.num_workers = min(multiprocessing.cpu_count(), num_workers)
        self.pointcloud_pool = ProcessPoolExecutor(max_workers=self.num_workers)

        if self.enable_llm:
            self._init_models()

    def _init_models(self):
        from openai import OpenAI
        import whisper

        self.llm_client = OpenAI(api_key=config.OPENAI_API_KEY)
        self.whisper_model = whisper.load_model(config.WHISPER_MODEL)

        from llm_voting import SemanticEntropyVoting
        import models as voting_models

        if voting_models.embeddings_model is None:
            voting_models.init_models()

        voting_system = SemanticEntropyVoting(
            embeddings_model=voting_models.embeddings_model,
            tokenizer=voting_models.tokenizer
        )

        self.llm_caller = LLMCaller(
            self.llm_client, voting_system,
            confidence_threshold=-0.003, alpha=0.1
        )

    async def process_log(self, data):
        try:
            full_ubr = self._enrich_ubr(data)
            full_ubr = self._calculate_continuous_action_duration(full_ubr)

            referent = full_ubr.get('ActionReferentName', '')
            referent_type = full_ubr.get('ActionReferentType', '')
            user = full_ubr.get('User', 'default')

            if referent == 'PostDefined' and referent_type == 'Physical':
                referent_body = full_ubr.get('ActionReferent', '')
                ref_key = Path(referent_body).stem if referent_body else ''
                if not ref_key:
                    return
                if ref_key in self.ar_referent_results:
                    full_ubr['ActionReferentName'] = self.ar_referent_results[ref_key]
                else:
                    self.pending_logs.append({
                        'data': data, 'full_ubr': full_ubr,
                        'referent_name': ref_key, 'user': user
                    })
                    logger.info(f"[Log] AR ref '{ref_key}' waiting for ARPNG")
                    return
            elif referent and referent != 'PostDefined':
                if not self._check_referent_exists(referent, user):
                    self.pending_logs.append({
                        'data': data, 'full_ubr': full_ubr,
                        'referent_name': referent, 'user': user
                    })
                    return

            insight_result = await self.llm_caller.generate_ubr_insight(full_ubr)

            if insight_result:
                full_ubr['insight'] = insight_result['insight']
                full_ubr['insight_status'] = insight_result['status']

                if self.frontend_notifier:
                    await self._notify_frontend_new_log(full_ubr, data.seq, insight_result)

                if insight_result['status'] == 'pending':
                    asyncio.create_task(self._handle_cloud_voting(full_ubr, data))
            else:
                full_ubr['insight_status'] = 'error'

            self.log_queue.append(data)
        except Exception as e:
            logger.error(f"[Log] process failed: {e}")

    async def _handle_cloud_voting(self, full_ubr: dict, data):
        try:
            refined = await self.llm_caller.refine_insight_with_voting(full_ubr)

            if refined:
                full_ubr['insight'] = refined['insight']
                full_ubr['insight_status'] = 'completed'

                if self.frontend_notifier:
                    await self._notify_frontend_insight_update(data.seq, refined)
        except Exception as e:
            logger.error(f"[Cloud voting] seq={data.seq} failed: {e}")

    async def process_context(self, data):
        try:
            self.cached_context = data

            if data.color_data:
                rgb_path = self.output_dir / "context" / data.color_filename
                asyncio.create_task(asyncio.to_thread(self._write_binary_file, rgb_path, data.color_data))

                desc = await self.llm_caller.get_context_description(data.color_data, data.seq)
                if desc:
                    self.cached_action_description = desc

            if data.depth_data and data.camera_json:
                file_num = self._extract_file_number(data.color_filename) if data.color_filename else data.seq
                user = getattr(data, 'user', 'User1')
                asyncio.create_task(self._process_point_cloud(
                    data.color_data, data.depth_data, data.camera_json,
                    data.timestamp, data.seq, file_num, "context", user
                ))

        except Exception as e:
            logger.error(f"[Context] failed: {e}")

    async def process_hand(self, data):
        try:
            if data.hand_data:
                path = self.output_dir / "hand" / data.hand_filename
                asyncio.create_task(asyncio.to_thread(self._write_binary_file, path, data.hand_data))

            if data.depth_data and data.camera_json:
                file_num = self._extract_file_number(data.hand_filename) if data.hand_filename else data.seq
                user = getattr(data, 'user', 'User1')
                asyncio.create_task(self._process_point_cloud(
                    data.hand_data, data.depth_data, data.camera_json,
                    data.timestamp, data.seq, file_num, "hand", user
                ))

        except Exception as e:
            logger.error(f"[Hand] failed: {e}")

    async def process_voice(self, data):
        try:
            user = getattr(data, 'user', 'User1')
            user_dir = self.output_dir / "voice" / user
            user_dir.mkdir(parents=True, exist_ok=True)
            asyncio.create_task(asyncio.to_thread(
                self._write_binary_file, user_dir / data.filename, data.audio_data))

            text = await asyncio.to_thread(self._whisper_transcribe, data.audio_data)
            if text and self.enable_llm:
                await self.llm_caller.get_voice_intent(text, data.seq)
        except Exception as e:
            logger.error(f"[Voice] failed: {e}")

    async def process_object(self, data):
        try:
            user = getattr(data, 'user', 'default')

            user_dir = self.reference_dir / user
            user_dir.mkdir(parents=True, exist_ok=True)
            glb_path = user_dir / data.filename

            await asyncio.to_thread(self._write_binary_file, glb_path, data.glb_data)

            if self.webapp_reference_dir:
                webapp_user_dir = self.webapp_reference_dir / user
                webapp_user_dir.mkdir(parents=True, exist_ok=True)
                await asyncio.to_thread(self._write_binary_file, webapp_user_dir / data.filename, data.glb_data)

            obj_name = data.filename.replace('.glb', '')
            await self._process_pending_logs_for_referent(obj_name, user)

            if self.frontend_notifier:
                await self._notify_frontend_object_ready(data.filename, obj_name, user)
        except Exception as e:
            logger.error(f"[Object] failed: {e}")

    async def process_referent_image(self, data):
        try:
            user = getattr(data, 'user', 'default')
            ref_key = data.filename.replace('.png', '').replace('_arpng', '')
            detected_name, confidence = await asyncio.to_thread(
                self._yolo_detect_sync, data.color_data)
            self.ar_referent_results[ref_key] = detected_name
            logger.info(f"[ARReferent] {data.filename} -> {detected_name} ({confidence:.4f})")

            for item in self.pending_logs:
                if item['referent_name'] == ref_key and item.get('user', 'default') == user:
                    item['full_ubr']['ActionReferentName'] = detected_name

            await self._process_pending_logs_for_referent(ref_key, user)
        except Exception as e:
            logger.error(f"[ReferentImage] failed: {e}")

    async def _process_point_cloud(self, color_data, depth_data, camera_json,
                                   timestamp, seq, file_number, data_type, user="User1"):
        try:
            output_name = f"{data_type}_{timestamp}_seq{file_number}"
            glb_dir = str(self.webapp_context_dir) if data_type == "context" else str(self.webapp_hand_dir)

            loop = asyncio.get_event_loop()
            result = await loop.run_in_executor(
                self.pointcloud_pool, reconstruct_point_cloud_task,
                color_data, depth_data, camera_json, output_name, data_type,
                glb_dir, str(self.output_dir)
            )

            if result['success']:
                notify_path = result['webapp_relative_path'] or result['glb_path']
                self._on_glb_created_callback(notify_path, data_type, output_name, user)
        except Exception as e:
            logger.error(f"[{data_type} GLB] seq={seq} error: {e}")

    def _enrich_ubr(self, data) -> dict:
        full_ubr = data.ubr_json.copy()
        if self.cached_action_description:
            full_ubr['ActionContextDescription'] = self.cached_action_description
        return full_ubr

    def _calculate_continuous_action_duration(self, ubr: dict) -> dict:
        action_bit = ubr.get('ActionTypeAndIdBit', 0)
        if action_bit == 0:
            return ubr
        ts = ubr.get('Timestamp', '')
        current_time = self._parse_timestamp_to_seconds(ts)
        if action_bit not in self.continuous_actions:
            self.continuous_actions[action_bit] = {'start_timestamp': ts, 'start_time': current_time}
            ubr['Duration'] = "000000_000000_000000"
        else:
            start = self.continuous_actions.pop(action_bit)
            duration = current_time - start['start_time']
            ubr['Duration'] = f"000000_000000_{int(duration * 1_000_000):06d}"
        return ubr

    def _parse_timestamp_to_seconds(self, timestamp: str) -> float:
        if not timestamp or len(timestamp) < 15:
            return 0.0
        tp = timestamp[7:13]
        ms = timestamp[14:] if len(timestamp) > 14 else '0'
        return int(tp[:2]) * 3600 + int(tp[2:4]) * 60 + int(tp[4:6]) + int(ms) / 1000.0

    def _whisper_transcribe(self, audio_data: bytes) -> Optional[str]:
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
            tmp.write(audio_data)
            tmp_path = tmp.name
        try:
            result = self.whisper_model.transcribe(tmp_path, language='zh', fp16=False, task='transcribe')
            return result["text"].strip()
        finally:
            if os.path.exists(tmp_path):
                os.remove(tmp_path)

    def _extract_file_number(self, filename: str) -> int:
        match = re.search(r'(\d+)', filename)
        return int(match.group(1)) if match else 0

    def _yolo_detect_sync(self, image_data: bytes) -> tuple:
        nparr = np.frombuffer(image_data, np.uint8)
        img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
        results = self.yolo_model.predict(
            source=img, imgsz=config.YOLO_IMGSZ,
            conf=config.YOLO_CONF_THRESHOLD,
            device=config.YOLO_DEVICE, verbose=False)
        if results and len(results[0].boxes) > 0:
            best_idx = int(results[0].boxes.conf.argmax())
            best_box = results[0].boxes[best_idx]
            class_id = int(best_box.cls[0])
            confidence = float(best_box.conf[0])
            class_name = results[0].names[class_id]
            return class_name, confidence
        return "Unknown", 0.0

    def _check_referent_exists(self, referent_name: str, user: str = 'default') -> bool:
        if not referent_name:
            return True
        return (self.reference_dir / user / f"{referent_name}.glb").exists()

    async def _process_pending_logs_for_referent(self, referent_name: str, user: str = 'default'):
        processed = []
        for i, item in enumerate(self.pending_logs):
            if item['referent_name'] == referent_name and item.get('user', 'default') == user:
                asyncio.create_task(self._process_pending_log(item['data'], item['full_ubr']))
                processed.append(i)
        for idx in reversed(processed):
            self.pending_logs.remove(self.pending_logs[idx])

    async def _process_pending_log(self, data, full_ubr: dict):
        try:
            insight_result = await self.llm_caller.generate_ubr_insight(full_ubr)

            if insight_result:
                full_ubr['insight'] = insight_result['insight']
                full_ubr['insight_status'] = insight_result['status']

                if self.frontend_notifier:
                    await self._notify_frontend_new_log(full_ubr, data.seq, insight_result)
                if insight_result['status'] == 'pending':
                    asyncio.create_task(self._handle_cloud_voting(full_ubr, data))

        except Exception as e:
            logger.error(f"[Pending Log] failed: {e}")

    def _write_binary_file(self, path: Path, data: bytes):
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, 'wb') as f:
            f.write(data)

    def _on_glb_created_callback(self, glb_path: str, data_type: str, output_name: str, user: str = "User1"):
        if self.frontend_notifier:
            msg = {"type": "new_glb", "data_type": data_type, "glb_path": glb_path,
                   "output_name": output_name, "user": user}
            asyncio.run_coroutine_threadsafe(self.frontend_notifier.notify_clients(msg), self.event_loop)

    async def _notify_frontend_new_log(self, full_ubr: dict, seq: int, insight_result: dict):
        timestamp = full_ubr.get('Timestamp', '')
        if timestamp and len(timestamp) >= 15:
            tp = timestamp[7:13]
            ms = timestamp[14:] if len(timestamp) > 14 else "000"
            start_time = f"{tp[:2]}:{tp[2:4]}:{tp[4:6]}.{ms.zfill(6)}"
        else:
            start_time = "00:00:00.000000"

        event_message = {
            "type": "new_log_event",
            "seq": seq,
            "start_time": start_time,
            "insight": insight_result['insight'],
            "insight_status": insight_result['status'],
            "event": {
                "User": full_ubr.get('User', 'User1'),
                "Name": full_ubr.get('UserAction', 'Unknown Action'),
                "Duration": full_ubr.get('Duration', '0_000000_000'),
                "ReferentType": full_ubr.get('ActionReferentType', 'Virtual'),
                "Intent": full_ubr.get('UserIntent', 'undefined'),
                "TriggerSource": full_ubr.get('ActionTriggerSource', 'undefined'),
                "Data": [{
                    "ActionInvokeTimestamp": start_time,
                    "ActionInvokeLocation": full_ubr.get('Location', ''),
                    "ActionContext": full_ubr.get('ActionContext', None),
                    "ActionContextDescription": full_ubr.get('ActionContextDescription', None),
                    "ActionReferentBody": full_ubr.get('ActionReferent', None),
                    "ActionReferentName": full_ubr.get('ActionReferentName', None),
                    "ActionReferentLocation": full_ubr.get('ActionReferentTransform', None),
                    "VoiceTranscription": full_ubr.get('VoiceTranscription', None),
                }],
                "insight": insight_result['insight'],
                "insight_status": insight_result['status'],
                "seq": seq,
                "_startTimeSort": start_time
            }
        }
        await self.frontend_notifier.notify_clients(event_message)

    async def _notify_frontend_insight_update(self, seq: int, insight_result: dict):
        msg = {"type": "insight_update", "seq": seq,
               "insight": insight_result['insight'], "insight_status": "completed"}
        await self.frontend_notifier.notify_clients(msg)

    async def _notify_frontend_object_ready(self, filename: str, object_name: str, user: str = 'default'):
        msg = {"type": "object_ready", "filename": filename, "object_name": object_name,
               "user": user, "glb_path": f"obj/reference/{user}/{filename}"}
        await self.frontend_notifier.notify_clients(msg)

    async def shutdown(self):
        self.pointcloud_pool.shutdown(wait=False)
        await self.llm_caller.close()
