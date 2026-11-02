"""Shared runtime switch and audit log for the VR baseline experiment."""

from __future__ import annotations

import csv
import json
import os
import threading
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional


FULL_HALAS = "full_halas"
NAIVE_ONLINE_LLM = "naive_online_llm"
EDGE_ONLY = "edge_only"
NO_AOI_EDGE = "no_aoi_edge"
RULE_BASED = "rule_based"
EXPLAINABLE_XR_OFFLINE = "explainable_xr_offline"

VALID_MODES = {
    FULL_HALAS,
    NAIVE_ONLINE_LLM,
    EDGE_ONLY,
    NO_AOI_EDGE,
    RULE_BASED,
    EXPLAINABLE_XR_OFFLINE,
}

ALIASES = {
    "full": FULL_HALAS,
    "halas": FULL_HALAS,
    "naive": NAIVE_ONLINE_LLM,
    "no_aoi_edge_pipeline": NO_AOI_EDGE,
    "explainable_xr": EXPLAINABLE_XR_OFFLINE,
}


def normalize_mode(value: Optional[str]) -> str:
    normalized = str(value or "").strip().lower().replace("-", "_").replace(" ", "_")
    normalized = ALIASES.get(normalized, normalized)
    return normalized if normalized in VALID_MODES else FULL_HALAS


@dataclass(frozen=True)
class BaselinePolicy:
    mode: str
    unity_aoi_enabled: bool
    edge_enabled: bool
    direct_cloud: bool
    cloud_fallback: bool
    interaction_only: bool
    offline: bool = False


POLICIES = {
    FULL_HALAS: BaselinePolicy(
        FULL_HALAS, True, True, False, True, False),
    NAIVE_ONLINE_LLM: BaselinePolicy(
        NAIVE_ONLINE_LLM, False, False, True, False, False),
    EDGE_ONLY: BaselinePolicy(
        EDGE_ONLY, True, True, False, False, False),
    NO_AOI_EDGE: BaselinePolicy(
        NO_AOI_EDGE, False, True, False, True, False),
    RULE_BASED: BaselinePolicy(
        RULE_BASED, False, False, False, False, False),
    EXPLAINABLE_XR_OFFLINE: BaselinePolicy(
        EXPLAINABLE_XR_OFFLINE, False, False, False, False, False, True),
}


def policy_for_mode(mode: Optional[str]) -> BaselinePolicy:
    return POLICIES[normalize_mode(mode)]


def default_shared_config_path() -> Path:
    configured = os.environ.get("HALAS_BASELINE_CONFIG_PATH", "").strip()
    if configured:
        return Path(configured).expanduser()
    return Path(__file__).resolve().parents[2] / "config" / "vr_baseline_run.json"


class BaselineController:
    """Reloads Unity's shared config when it changes and writes route-level metrics."""

    FIELDNAMES = [
        "recorded_utc",
        "run_id",
        "mode",
        "seq",
        "action_id",
        "phase",
        "fragment_seq",
        "user_action",
        "user_intent",
        "expected_ood",
        "ground_truth_aoi",
        "event_kind",
        "model_route",
        "edge_confidence_score",
        "gate_would_defer",
        "cloud_called",
        "cloud_call_count",
        "cloud_prompt_tokens",
        "cloud_completion_tokens",
        "cloud_cost_usd",
        "edge_latency_ms",
        "edge_latency_raw_ms",
        "cloud_latency_ms",
        "b2i_latency_ms",
        "processing_latency_ms",
        "status",
        "final_insight",
        "detail",
    ]

    def __init__(self, output_dir: Path, config_path: Optional[Path] = None):
        self.config_path = Path(config_path or default_shared_config_path())
        self.output_dir = Path(output_dir) / "baseline_metrics"
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._mtime_ns = -1
        self._config: Dict[str, Any] = {
            "mode": normalize_mode(os.environ.get("HALAS_BASELINE_MODE")),
            "run_id": os.environ.get("HALAS_BASELINE_RUN_ID", "manual"),
            "device": "VR",
            "communication_included": False,
        }
        self.refresh(force=True)

    def refresh(self, force: bool = False) -> Dict[str, Any]:
        try:
            stat = self.config_path.stat()
            if not force and stat.st_mtime_ns == self._mtime_ns:
                return dict(self._config)
            with self.config_path.open("r", encoding="utf-8-sig") as stream:
                loaded = json.load(stream)
            if not isinstance(loaded, dict):
                raise ValueError("shared baseline config must contain a JSON object")
            loaded["mode"] = normalize_mode(loaded.get("mode"))
            loaded["run_id"] = str(loaded.get("run_id") or "manual")
            loaded["communication_included"] = bool(
                loaded.get("communication_included", False)
            )
            self._config = loaded
            self._mtime_ns = stat.st_mtime_ns
        except FileNotFoundError:
            pass
        except (OSError, ValueError, json.JSONDecodeError):
            # Keep the last valid snapshot while Unity atomically rewrites the file.
            pass
        return dict(self._config)

    @property
    def config(self) -> Dict[str, Any]:
        return self.refresh()

    @property
    def policy(self) -> BaselinePolicy:
        return policy_for_mode(self.refresh().get("mode"))

    @property
    def run_id(self) -> str:
        return str(self.refresh().get("run_id") or "manual")

    def allows_ubr(self, full_ubr: Dict[str, Any]) -> bool:
        policy = self.policy
        if policy.offline:
            return False
        if not policy.interaction_only:
            return True
        intent = str(full_ubr.get("UserIntent", "")).strip().lower()
        action = str(full_ubr.get("UserAction", "")).strip().lower()
        trigger = str(full_ubr.get("ActionTriggerSource", "")).strip().lower()
        return (
            "interaction" in intent
            or "communication" in intent
            or "voice" in intent
            or "speech" in action
            or "voice" in trigger
        )

    @staticmethod
    def is_expected_ood(full_ubr: Dict[str, Any]) -> bool:
        if "ExpectedOOD" in full_ubr:
            return bool(full_ubr.get("ExpectedOOD"))
        action = str(full_ubr.get("UserAction", "")).strip().upper()
        return action.startswith("OOD_")

    def record(
        self,
        *,
        full_ubr: Optional[Dict[str, Any]] = None,
        seq: int = 0,
        phase: str = "",
        fragment_seq: int = -1,
        event_kind: str,
        result: Optional[Dict[str, Any]] = None,
        processing_latency_ms: Optional[float] = None,
        detail: str = "",
    ) -> None:
        config = self.refresh()
        ubr = full_ubr or {}
        mode = normalize_mode(ubr.get("BaselineMode") or config.get("mode"))
        run_id = str(
            ubr.get("_BaselineRunId") or config.get("run_id") or "manual"
        )
        model = result or {}
        action_type_and_id = int(ubr.get("ActionTypeAndIdBit", 0) or 0)
        row = {
            "recorded_utc": datetime.now(timezone.utc).isoformat(),
            "run_id": run_id,
            "mode": mode,
            "seq": seq,
            "action_id": action_type_and_id & 0xFFFFFF,
            "phase": phase,
            "fragment_seq": fragment_seq,
            "user_action": ubr.get("UserAction", ""),
            "user_intent": ubr.get("UserIntent", ""),
            "expected_ood": int(self.is_expected_ood(ubr)),
            "ground_truth_aoi": int(bool(ubr.get("GroundTruthAoI", False))),
            "event_kind": event_kind,
            "model_route": model.get("model_route", ""),
            "edge_confidence_score": model.get("edge_confidence_score", ""),
            "gate_would_defer": int(bool(model.get("gate_would_defer", False))),
            "cloud_called": int(bool(model.get("cloud_called", False))),
            "cloud_call_count": int(model.get("cloud_call_count", 0) or 0),
            "cloud_prompt_tokens": int(model.get("cloud_prompt_tokens", 0) or 0),
            "cloud_completion_tokens": int(
                model.get("cloud_completion_tokens", 0) or 0
            ),
            "cloud_cost_usd": model.get("cloud_cost_usd", ""),
            "edge_latency_ms": model.get("edge_latency_ms", ""),
            "edge_latency_raw_ms": model.get("edge_latency_raw_ms", ""),
            "cloud_latency_ms": model.get("cloud_latency_ms", ""),
            "b2i_latency_ms": model.get("b2i_latency_ms", ""),
            "processing_latency_ms": (
                "" if processing_latency_ms is None else round(processing_latency_ms, 4)
            ),
            "status": model.get("status", ""),
            "final_insight": model.get("insight", ""),
            "detail": detail,
        }
        path = self.output_dir / f"{safe_filename(run_id)}_server_routes.csv"
        with self._lock:
            write_header = not path.exists()
            with path.open("a", encoding="utf-8", newline="") as stream:
                writer = csv.DictWriter(stream, fieldnames=self.FIELDNAMES)
                if write_header:
                    writer.writeheader()
                writer.writerow(row)


def safe_filename(value: str) -> str:
    safe = "".join(
        character if character.isalnum() or character in ("-", "_", ".") else "_"
        for character in str(value)
    )
    return safe or "manual"
