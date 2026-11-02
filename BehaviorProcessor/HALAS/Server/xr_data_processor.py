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
import time
from concurrent.futures import ProcessPoolExecutor
import multiprocessing
try:
    from ultralytics import YOLO
except Exception:
    YOLO = None

project_root = Path(__file__).resolve().parent.parent.parent
llm_dir = str(Path(__file__).resolve().parent.parent / "LLM")
sys.path.insert(0, str(project_root))
sys.path.insert(0, llm_dir)

import config
from baseline_modes import (
    BaselineController,
    EDGE_ONLY,
    NAIVE_ONLINE_LLM,
    RULE_BASED,
    policy_for_mode,
)
from llm_caller import LLMCaller
try:
    from point_cloud_worker import reconstruct_point_cloud_task
except Exception:
    reconstruct_point_cloud_task = None

logger = logging.getLogger(__name__)

DEFAULT_XR_ACTION_CONTEXT_DESCRIPTION = ""


def _ignore_worker_sigint():
    import signal
    signal.signal(signal.SIGINT, signal.SIG_IGN)


class XRDataProcessor:
    def __init__(self, output_dir="./output", webapp_root=None, frontend_notifier=None,
                 num_workers=2, reference_dir=None):
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)
        for subdir in ["logs", "context", "hand", "voice", "glb"]:
            (self.output_dir / subdir).mkdir(exist_ok=True)
        baseline_config_path = (
            Path(config.BASELINE_CONFIG_PATH)
            if config.BASELINE_CONFIG_PATH else None
        )
        baseline_metrics_dir = (
            Path(config.BASELINE_METRICS_DIR)
            if config.BASELINE_METRICS_DIR else self.output_dir
        )
        self.baseline_controller = BaselineController(
            baseline_metrics_dir, config_path=baseline_config_path)

        self.reference_dir = Path(reference_dir) if reference_dir else Path(__file__).parent.parent / "Reference"
        self.reference_dir.mkdir(parents=True, exist_ok=True)
        self.pending_logs = deque()
        self.ar_referent_results = {}
        self.yolo_model = None
        if YOLO is None or not Path(config.YOLO_MODEL_PATH).is_file():
            logger.info("[YOLO] disabled: install ultralytics and configure a local model file")
        else:
            try:
                self.yolo_model = YOLO(config.YOLO_MODEL_PATH)
                logger.info(f"[YOLO] Loaded model: {config.YOLO_MODEL_PATH}")
            except Exception as e:
                logger.warning(f"[YOLO] disabled: {e}")

        self.webapp_reference_dir = Path(webapp_root) / "obj" / "reference" if webapp_root else None
        if self.webapp_reference_dir:
            self.webapp_reference_dir.mkdir(parents=True, exist_ok=True)

        self.log_queue = deque(maxlen=1000)
        self.enable_llm = True
        self.llm_client = None
        self.whisper_model = None
        self.llm_caller = None
        self.cached_context = None
        self.cached_action_description = DEFAULT_XR_ACTION_CONTEXT_DESCRIPTION
        self.frontend_notifier = frontend_notifier
        self.event_loop = asyncio.get_event_loop()
        self.background_tasks = set()

        if webapp_root:
            self.webapp_realtime_dir = Path(webapp_root) / "obj" / "realtime"
            self.webapp_context_dir = self.webapp_realtime_dir / "context"
            self.webapp_hand_dir = self.webapp_realtime_dir / "hand"
            self.webapp_realtime_dir.mkdir(parents=True, exist_ok=True)
            self.webapp_context_dir.mkdir(parents=True, exist_ok=True)
            self.webapp_hand_dir.mkdir(parents=True, exist_ok=True)
        else:
            self.webapp_realtime_dir = None
            self.webapp_context_dir = None
            self.webapp_hand_dir = None

        self.num_workers = min(multiprocessing.cpu_count(), num_workers)
        self.pointcloud_pool = ProcessPoolExecutor(
            max_workers=self.num_workers,
            initializer=_ignore_worker_sigint
        )

        if self.enable_llm:
            try:
                self._init_models()
            except Exception as e:
                self.enable_llm = False
                self.llm_caller = None
                logger.warning(f"[LLM] disabled, using interface placeholder: {e}")

    def _init_models(self):
        self.llm_caller = LLMCaller(
            None, None,
            confidence_threshold=config.TOKEN_CONFIDENCE_THRESHOLD,
            alpha=config.TOKEN_CONFIDENCE_ALPHA
        )

    async def process_log(self, data):
        processing_started = float(
            getattr(data, 'behavior_occurred_perf', 0.0)
            or getattr(data, 'received_perf', 0.0)
            or time.perf_counter()
        )
        try:
            full_ubr = self._enrich_ubr(data)
            policy = self.baseline_controller.policy
            full_ubr['BaselineMode'] = policy.mode
            full_ubr['_BaselineRunId'] = self.baseline_controller.run_id
            # Each received UBR is already one complete model input. Do not
            # reconstruct Begin/Continue/End episodes or mutate its Duration.
            full_ubr['_B2IStartedPerf'] = processing_started

            if not self.baseline_controller.allows_ubr(full_ubr):
                self.baseline_controller.record(
                    full_ubr=full_ubr,
                    seq=data.seq,
                    phase=data.phase,
                    fragment_seq=data.fragment_seq,
                    event_kind="filtered",
                    result={
                        "status": "filtered",
                        "model_route": (
                            "offline_excluded" if policy.offline
                            else "rule_filter"
                        ),
                    },
                    processing_latency_ms=(
                        time.perf_counter() - processing_started) * 1000.0,
                    detail=(
                        "Explainable XR is offline"
                        if policy.offline
                        else "Rule-Based keeps Interaction/Communication only"
                    ),
                )
                return

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
            elif (
                referent
                and referent != 'PostDefined'
                and not bool(full_ubr.get("ExperimentReplay", False))
            ):
                provisional = str(getattr(data, 'aoi_state', '')).lower() == 'provisional'
                if not provisional and not self._check_referent_exists(referent, user):
                    self.pending_logs.append({
                        'data': data, 'full_ubr': full_ubr,
                        'referent_name': referent, 'user': user
                    })
                    return

            insight_result = await self._generate_insight(
                full_ubr, data.phase, data.fragment_seq)

            if insight_result:
                full_ubr['insight'] = insight_result['insight']
                full_ubr['insight_status'] = insight_result['status']
                full_ubr['baseline_mode'] = insight_result.get(
                    'baseline_mode', policy.mode)
                full_ubr['model_route'] = insight_result.get('model_route', '')

                if self.frontend_notifier:
                    await self._notify_frontend_new_log(
                        full_ubr, data.seq, insight_result,
                        data.phase, data.fragment_seq)
                insight_result['b2i_latency_ms'] = (
                    time.perf_counter() - processing_started
                ) * 1000.0

                self.baseline_controller.record(
                    full_ubr=full_ubr,
                    seq=data.seq,
                    phase=data.phase,
                    fragment_seq=data.fragment_seq,
                    event_kind="initial_insight",
                    result=insight_result,
                    processing_latency_ms=insight_result['b2i_latency_ms'],
                )

                if insight_result['status'] == 'pending':
                    self._start_background_task(
                        self._handle_cloud_voting(full_ubr, data)
                    )
            else:
                full_ubr['insight_status'] = 'error'

            self.log_queue.append(data)
        except Exception as e:
            logger.error(f"[Log] process failed: {e}")

    async def process_aoi_cancel(self, data):
        """Forward an explicit XR-side rejection without invoking either model."""
        if not self.frontend_notifier or data.action_id <= 0:
            return
        await self.frontend_notifier.notify_clients({
            "type": "aoi_cancel",
            "seq": data.seq,
            "action_id": data.action_id,
            "fragment_seq": data.fragment_seq,
            "status": "rejected",
            "reason": "RejectedByAoI"
        })

    async def _handle_cloud_voting(self, full_ubr: dict, data):
        if self.llm_caller is None:
            return
        policy = policy_for_mode(full_ubr.get('baseline_mode'))
        if not policy.cloud_fallback:
            return
        started = time.perf_counter()
        try:
            refined = await self.llm_caller.refine_insight_with_voting(
                full_ubr, data.phase, data.fragment_seq)

            if refined:
                refined['baseline_mode'] = policy.mode
                refined['b2i_latency_ms'] = (
                    time.perf_counter()
                    - float(full_ubr.get('_B2IStartedPerf', started))
                ) * 1000.0
                full_ubr['insight'] = refined['insight']
                full_ubr['insight_status'] = 'completed'
                full_ubr['consensus_score'] = refined.get('consensus_score')
                full_ubr['consensus_source'] = refined.get('consensus_source')
                full_ubr['model_route'] = refined.get('model_route', '')

                if self.frontend_notifier:
                    await self._notify_frontend_insight_update(data.seq, refined, full_ubr)
                refined['b2i_latency_ms'] = (
                    time.perf_counter()
                    - float(full_ubr.get('_B2IStartedPerf', started))
                ) * 1000.0
                self.baseline_controller.record(
                    full_ubr=full_ubr,
                    seq=data.seq,
                    phase=data.phase,
                    fragment_seq=data.fragment_seq,
                    event_kind="cloud_completion",
                    result=refined,
                    processing_latency_ms=(
                        time.perf_counter() - started) * 1000.0,
                )
            else:
                failure = {
                    "insight": full_ubr.get("insight", "None Cloud"),
                    "status": "error",
                    "baseline_mode": policy.mode,
                    "model_route": "cloud_voting_failed",
                    "cloud_called": True,
                    "cloud_call_count": 3,
                    "b2i_latency_ms": (
                        time.perf_counter()
                        - float(full_ubr.get('_B2IStartedPerf', started))
                    ) * 1000.0,
                }
                self.baseline_controller.record(
                    full_ubr=full_ubr,
                    seq=data.seq,
                    phase=data.phase,
                    fragment_seq=data.fragment_seq,
                    event_kind="cloud_failure",
                    result=failure,
                    processing_latency_ms=failure["b2i_latency_ms"],
                    detail="No cloud provider returned a usable insight",
                )
        except Exception as e:
            logger.error(f"[Cloud voting] seq={data.seq} failed: {e}")

    async def process_context(self, data):
        try:
            self.cached_context = data

            if data.color_data:
                rgb_path = self.output_dir / "context" / data.color_filename
                asyncio.create_task(asyncio.to_thread(self._write_binary_file, rgb_path, data.color_data))

                if self.llm_caller:
                    desc = await self.llm_caller.get_context_description(data.color_data, data.seq)
                    if desc:
                        self.cached_action_description = self._scene_aligned_context_description(desc)

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

            if self.whisper_model is None:
                try:
                    import whisper
                    self.whisper_model = whisper.load_model(config.WHISPER_MODEL)
                except Exception as exc:
                    logger.warning(f"[Whisper] disabled: {exc}")
            text = await asyncio.to_thread(
                self._whisper_transcribe, data.audio_data
            ) if self.whisper_model else None
            if text and self.llm_caller:
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
        if reconstruct_point_cloud_task is None:
            logger.warning(f"[{data_type} GLB] skipped: point cloud worker dependencies are unavailable")
            return
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
                logger.info(
                    f"[{data_type} GLB] seq={seq} created: {notify_path} "
                    f"points={result.get('point_count', 0)}"
                )
                self._update_latest_model_manifest(data_type, notify_path, output_name, user)
                await self._notify_frontend_new_glb(notify_path, data_type, output_name, user)
                if data_type == "context":
                    await asyncio.to_thread(
                        self._cleanup_old_glbs,
                        self.webapp_context_dir,
                        config.MAX_CONTEXT_GLB_FILES
                    )
            else:
                logger.warning(
                    f"[{data_type} GLB] seq={seq} skipped: "
                    f"{result.get('error', 'unknown error')}"
                )
        except Exception as e:
            logger.error(f"[{data_type} GLB] seq={seq} error: {e}")

    def _enrich_ubr(self, data) -> dict:
        full_ubr = data.ubr_json.copy()
        if self.cached_action_description:
            full_ubr['ActionContextDescription'] = self.cached_action_description
        return full_ubr

    @staticmethod
    def _scene_aligned_context_description(description: str) -> str:
        """Normalize supplied context text without adding unobserved scene details."""
        text = re.sub(r"\s+", " ", str(description or "")).strip()
        if not text:
            return DEFAULT_XR_ACTION_CONTEXT_DESCRIPTION
        return text.rstrip(". ") + "."

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
        if self.yolo_model is None:
            return "Unknown", 0.0
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
            insight_result = await self._generate_insight(
                full_ubr, data.phase, data.fragment_seq)

            if insight_result:
                full_ubr['insight'] = insight_result['insight']
                full_ubr['insight_status'] = insight_result['status']

                if self.frontend_notifier:
                    await self._notify_frontend_new_log(full_ubr, data.seq, insight_result)
                if insight_result['status'] == 'pending':
                    self._start_background_task(
                        self._handle_cloud_voting(full_ubr, data)
                    )

        except Exception as e:
            logger.error(f"[Pending Log] failed: {e}")

    def _write_binary_file(self, path: Path, data: bytes):
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, 'wb') as f:
            f.write(data)

    def _start_background_task(self, coroutine):
        task = asyncio.create_task(coroutine)
        self.background_tasks.add(task)
        task.add_done_callback(self.background_tasks.discard)
        return task

    def _cleanup_old_glbs(self, directory: Optional[Path], keep_count: int):
        if not directory or keep_count <= 0:
            return
        try:
            files = sorted(
                directory.glob("*.glb"),
                key=lambda path: path.stat().st_mtime,
                reverse=True
            )
            for old_file in files[keep_count:]:
                old_file.unlink(missing_ok=True)
            removed = max(0, len(files) - keep_count)
            if removed:
                logger.info(f"[Context GLB] cleanup removed {removed} old files")
        except Exception as e:
            logger.warning(f"[Context GLB] cleanup failed: {e}")

    def _update_latest_model_manifest(self, data_type: str, glb_path: str, output_name: str, user: str):
        if not self.webapp_realtime_dir:
            return
        try:
            manifest_path = self.webapp_realtime_dir / "latest_models.json"
            manifest = {}
            if manifest_path.exists():
                with open(manifest_path, 'r', encoding='utf-8-sig') as f:
                    manifest = json.load(f)
            key = "context" if data_type == "context" else "referent"
            manifest[key] = {
                "url": str(glb_path).replace("\\", "/"),
                "name": output_name,
                "user": user
            }
            with open(manifest_path, 'w', encoding='utf-8') as f:
                json.dump(manifest, f, indent=2, ensure_ascii=False)
        except Exception as e:
            logger.warning(f"[Model manifest] update failed: {e}")

    async def _notify_frontend_new_glb(self, glb_path: str, data_type: str, output_name: str, user: str = "User1"):
        if self.frontend_notifier:
            msg = {"type": "new_glb", "data_type": data_type, "glb_path": glb_path,
                   "output_name": output_name, "user": user}
            await self.frontend_notifier.notify_clients(msg)

    async def _notify_frontend_new_log(self, full_ubr: dict, seq: int,
                                       insight_result: dict, phase: str = "",
                                       fragment_seq: int = -1):
        timestamp = full_ubr.get('Timestamp', '')
        if timestamp and len(timestamp) >= 15:
            tp = timestamp[7:13]
            ms = timestamp[14:] if len(timestamp) > 14 else "000"
            start_time = f"{tp[:2]}:{tp[2:4]}:{tp[4:6]}.{ms.zfill(6)}"
        else:
            start_time = "00:00:00.000000"

        action_type_and_id = int(full_ubr.get('ActionTypeAndIdBit', 0) or 0)
        action_id = action_type_and_id & 0xFFFFFF
        normalized_phase = (phase or '').strip().lower()
        lifecycle_status = 'completed' if normalized_phase == 'end' else 'active'

        event_message = {
            "type": "new_log_event",
            "seq": seq,
            "action_id": action_id,
            "phase": phase,
            "fragment_seq": fragment_seq,
            "lifecycle_status": lifecycle_status,
            "start_time": start_time,
            "insight": insight_result['insight'],
            "insight_status": insight_result['status'],
            "baseline_mode": insight_result.get('baseline_mode'),
            "model_route": insight_result.get('model_route'),
            "edge_confidence_score": insight_result.get('edge_confidence_score'),
            "gate_would_defer": insight_result.get('gate_would_defer', False),
            "cloud_called": insight_result.get('cloud_called', False),
            "consensus_score": insight_result.get('consensus_score'),
            "consensus_source": insight_result.get('consensus_source'),
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
                "baselineMode": insight_result.get('baseline_mode'),
                "modelRoute": insight_result.get('model_route'),
                "edgeConfidenceScore": insight_result.get('edge_confidence_score'),
                "gateWouldDefer": insight_result.get('gate_would_defer', False),
                "cloudCalled": insight_result.get('cloud_called', False),
                "consensus_score": insight_result.get('consensus_score'),
                "consensus_source": insight_result.get('consensus_source'),
                "seq": seq,
                "actionId": action_id,
                "phase": phase,
                "fragmentSequence": fragment_seq,
                "lifecycleStatus": lifecycle_status,
                "_startTimeSort": start_time
            }
        }
        await self.frontend_notifier.notify_clients(event_message)

    async def _notify_frontend_insight_update(self, seq: int, insight_result: dict,
                                              full_ubr: dict = None):
        action_type_and_id = int((full_ubr or {}).get('ActionTypeAndIdBit', 0) or 0)
        msg = {"type": "insight_update", "seq": seq,
               "action_id": action_type_and_id & 0xFFFFFF,
               "insight": insight_result['insight'], "insight_status": "completed",
               "baseline_mode": insight_result.get('baseline_mode'),
               "model_route": insight_result.get('model_route'),
               "cloud_called": insight_result.get('cloud_called', False),
               "cloud_latency_ms": insight_result.get('cloud_latency_ms'),
               "consensus_score": insight_result.get('consensus_score'),
               "consensus_source": insight_result.get('consensus_source')}
        await self.frontend_notifier.notify_clients(msg)

    async def _notify_frontend_object_ready(self, filename: str, object_name: str, user: str = 'default'):
        msg = {"type": "object_ready", "filename": filename, "object_name": object_name,
               "user": user, "glb_path": f"obj/reference/{user}/{filename}"}
        await self.frontend_notifier.notify_clients(msg)

    async def _generate_insight(self, full_ubr: dict, phase: str = "",
                                fragment_seq: int = -1) -> dict:
        policy = self.baseline_controller.policy
        started = time.perf_counter()
        if policy.mode == RULE_BASED:
            return {
                "insight": self._rule_based_5w1h_insight(full_ubr, phase),
                "status": "completed",
                "baseline_mode": policy.mode,
                "model_route": "rule_5w1h",
                "cloud_called": False,
                "cloud_call_count": 0,
                "gate_would_defer": False,
                "b2i_latency_ms": (time.perf_counter() - started) * 1000.0,
            }
        if self.llm_caller:
            if policy.mode == NAIVE_ONLINE_LLM:
                result = await self.llm_caller.generate_cloud_insight(
                    full_ubr, phase, config.NAIVE_CLOUD_PROVIDER)
            elif policy.mode == EDGE_ONLY:
                result = await self.llm_caller.generate_edge_only_insight(
                    full_ubr, phase)
            elif policy.edge_enabled:
                result = await self.llm_caller.generate_ubr_insight(
                    full_ubr, phase, fragment_seq)
            else:
                result = None
            if result:
                result = dict(result)
                result['baseline_mode'] = policy.mode
                if result.get('status') == 'pending' and not policy.cloud_fallback:
                    result['status'] = 'completed'
                    result['model_route'] = (
                        'edge_forced_accept'
                        if result.get('tiny_available', False)
                        else 'edge_unavailable_no_fallback'
                    )
                    result['cloud_called'] = False
                    result['cloud_call_count'] = 0
                result['b2i_latency_ms'] = (
                    time.perf_counter()
                    - float(full_ubr.get('_B2IStartedPerf', started))
                ) * 1000.0
                return result
        return {
            "insight": "None Tiny",
            "status": "completed",
            "baseline_mode": policy.mode,
            "model_route": "model_interface_unavailable",
            "cloud_called": False,
            "gate_would_defer": False,
            "b2i_latency_ms": (time.perf_counter() - started) * 1000.0,
        }

    @staticmethod
    def _rule_based_5w1h_insight(full_ubr: dict, phase: str = "") -> str:
        """Deterministic 5W1H template; it never invokes an ML or cloud model."""
        who = str(full_ubr.get('User') or 'Unknown user')
        what = str(full_ubr.get('UserAction') or 'performed an XR behavior')
        referent = str(full_ubr.get('ActionReferentName') or 'no named referent')
        timestamp = str(full_ubr.get('Timestamp') or 'an unspecified time')
        duration = str(full_ubr.get('Duration') or 'unspecified')
        when = f"{timestamp}, duration {duration} seconds"
        where = str(full_ubr.get('Location') or 'an unspecified XR location')
        why = str(full_ubr.get('UserIntent') or 'an unspecified intent')
        how = str(full_ubr.get('ActionTriggerSource') or 'an unspecified input')
        lifecycle = f" ({phase})" if phase else ""
        return (
            f"Who: {who}. What: {what}{lifecycle} involving {referent}. "
            f"When: {when}. Where: {where}. Why: {why}. How: {how}."
        )

    def _fallback_insight(self, full_ubr: dict) -> str:
        action = full_ubr.get('UserAction', 'XR behavior')
        intent = full_ubr.get('UserIntent', 'undefined intent')
        referent = full_ubr.get('ActionReferentName', 'referent')
        trigger = full_ubr.get('ActionTriggerSource', 'XR device')
        return (
            f"Interface placeholder: {action} was captured from {trigger} "
            f"with intent '{intent}' and referent '{referent}'. Tiny-model "
            "and cloud refinement are available through the existing service interfaces."
        )

    async def shutdown(self):
        if self.background_tasks:
            await asyncio.gather(*list(self.background_tasks), return_exceptions=True)
            self.background_tasks.clear()
        if self.pointcloud_pool:
            self.pointcloud_pool.shutdown(wait=False, cancel_futures=True)
            self.pointcloud_pool = None
        if self.llm_caller:
            await self.llm_caller.close()
