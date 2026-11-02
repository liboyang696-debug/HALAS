import asyncio
import aiohttp
import numpy as np
import sys
from pathlib import Path
from typing import Optional, List, Dict
import logging
import json
import time

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "Server"))
import config

logger = logging.getLogger(__name__)

PROMPT_SCHEMA_VERSION = "ubr_insight_5000_training_v1"

INSIGHT_SYSTEM_PROMPT = """You are an XR user behavior Area of Interest analysis expert. Generate the topic and insight for the User Behavior Representation (UBR). The requirements are as follows:
1. The topic should be a summary of the insight.
2. The insight should be a comprehensive explanation that integrates the various components of the UBR.
3. You only need to generate the topic and insight."""

VOICE_INTENT_PROMPT = """You are an expert in analyzing the intention of a spoken word of a user. Given a transcribed speech text of a user, you need to output a summarized short text that best represents the user's intention for that speech. You must output only the answer, no explanation behind it with the answer. For example, if a text "That road is congested all the time, it would be difficult for people to use it as an evacuation route." was given, the plausible output would be "Revealing concerns of the use of the road for emergency scenario"."""


class LLMCaller:
    def __init__(self, llm_client, voting_system, confidence_threshold=-0.7, alpha=0.3):
        self.llm_client = llm_client
        self.voting_system = voting_system
        self.threshold = confidence_threshold
        self.alpha = alpha
        self.connector = None
        self.session = None
        self.cloud_semaphore = asyncio.Semaphore(config.MAX_CONCURRENT_LLM_CALLS)

    async def ensure_session(self):
        if self.session is None:
            self.connector = aiohttp.TCPConnector(limit=20, ttl_dns_cache=300)
            self.session = aiohttp.ClientSession(connector=self.connector)

    async def close(self):
        if self.session:
            await self.session.close()
        if self.connector:
            await self.connector.close()

    @staticmethod
    def _edge_latency_fields(raw_latency_ms: float) -> Dict:
        raw = max(0.0, float(raw_latency_ms))
        return {
            "edge_latency_ms": raw,
            "edge_latency_raw_ms": raw,
        }

    async def get_context_description(self, image_data: bytes, seq: int) -> Optional[str]:
        if not config.ENABLE_IMAGE2TEXT_MODEL:
            return None
        await self.ensure_session()
        form_data = aiohttp.FormData()
        form_data.add_field('image', image_data, filename=f"context_{seq}.png", content_type='image/png')
        form_data.add_field('max_length', str(40))
        form_data.add_field('return_confidence', 'True')
        try:
            async with self.session.post(
                config.IMAGE2TEXT_API_URL, data=form_data,
                timeout=aiohttp.ClientTimeout(total=20)
            ) as response:
                if response.status != 200:
                    return None
                result = await response.json()
                return result.get("caption", "")
        except Exception as e:
            logger.error(f"[Image2Text] seq={seq} error: {e}")
            return None

    async def generate_ubr_insight(self, full_ubr: dict, phase: str = "",
                                   fragment_seq: int = -1) -> Optional[Dict]:
        if not config.ENABLE_TINY_MODEL:
            return {
                "insight": "None Tiny",
                "status": "completed",
                "model_route": "edge_disabled",
                "tiny_available": False,
                "gate_would_defer": False,
                "cloud_called": False,
                **self._edge_latency_fields(0.0),
            }
        started = time.perf_counter()
        try:
            ubr_text = self._format_ubr_text(full_ubr, phase)
            insight, logprobs = await self._call_tiny_model(ubr_text, config.LLM_INSIGHT_API_URL)
            edge_latency_ms = (time.perf_counter() - started) * 1000.0
            if not insight:
                return {
                    "insight": "None Tiny",
                    "status": "pending",
                    "model_route": "edge_unavailable",
                    "tiny_available": False,
                    "gate_would_defer": True,
                    "cloud_called": False,
                    **self._edge_latency_fields(edge_latency_ms),
                }
            if insight == "None Tiny":
                return {
                    "insight": insight,
                    "status": "pending",
                    "model_route": "edge_unavailable",
                    "tiny_available": False,
                    "gate_would_defer": True,
                    "cloud_called": False,
                    **self._edge_latency_fields(edge_latency_ms),
                }
            confidence_score = (
                float(np.quantile(logprobs, self.alpha)) if logprobs else None
            )
            accepted = self._threshold_check(logprobs)
            return {
                "insight": insight,
                "status": "completed" if accepted else "pending",
                "model_route": "edge",
                "tiny_available": True,
                "edge_confidence_score": confidence_score,
                "gate_would_defer": not accepted,
                "cloud_called": False,
                **self._edge_latency_fields(edge_latency_ms),
            }
        except Exception as e:
            logger.info(f"[TinyModel] unavailable, using None Tiny: {e}")
            return {
                "insight": "None Tiny",
                "status": "pending",
                "model_route": "edge_unavailable",
                "tiny_available": False,
                "gate_would_defer": True,
                "cloud_called": False,
                **self._edge_latency_fields(
                    (time.perf_counter() - started) * 1000.0
                ),
            }

    async def generate_edge_only_insight(
        self, full_ubr: dict, phase: str = ""
    ) -> Dict:
        """Run MiniMind3 once without evaluating the token-confidence gate."""
        started = time.perf_counter()
        if not config.ENABLE_TINY_MODEL:
            return {
                "insight": "None Tiny",
                "status": "error",
                "model_route": "edge_only_unavailable",
                "tiny_available": False,
                "gate_would_defer": False,
                "cloud_called": False,
                "cloud_call_count": 0,
                **self._edge_latency_fields(0.0),
            }
        try:
            ubr_text = self._format_ubr_text(full_ubr, phase)
            insight, _ = await self._call_tiny_model(
                ubr_text, config.LLM_INSIGHT_API_URL, return_logprobs=False
            )
            latency_ms = (time.perf_counter() - started) * 1000.0
            if not insight or insight == "None Tiny":
                return {
                    "insight": "None Tiny",
                    "status": "error",
                    "model_route": "edge_only_unavailable",
                    "tiny_available": False,
                    "gate_would_defer": False,
                    "cloud_called": False,
                    "cloud_call_count": 0,
                    **self._edge_latency_fields(latency_ms),
                }
            return {
                "insight": insight,
                "status": "completed",
                "model_route": "edge_only",
                "tiny_available": True,
                "gate_would_defer": False,
                "cloud_called": False,
                "cloud_call_count": 0,
                **self._edge_latency_fields(latency_ms),
            }
        except Exception as exc:
            logger.error("[EdgeOnly] MiniMind3 failed: %s", exc)
            return {
                "insight": "None Tiny",
                "status": "error",
                "model_route": "edge_only_unavailable",
                "tiny_available": False,
                "gate_would_defer": False,
                "cloud_called": False,
                "cloud_call_count": 0,
                **self._edge_latency_fields(
                    (time.perf_counter() - started) * 1000.0
                ),
            }

    async def generate_cloud_insight(self, full_ubr: dict, phase: str = "",
                                     provider: str = "lanyi") -> Optional[Dict]:
        """Call exactly one cloud model for the Naive Online LLM baseline."""
        started = time.perf_counter()
        ubr_text = self._format_ubr_text(full_ubr, phase)
        normalized_provider = (provider or "lanyi").strip().lower()
        async with self.cloud_semaphore:
            if normalized_provider == "qwen":
                insight = await self._call_qwen(ubr_text)
            elif normalized_provider == "deepseek":
                insight = await self._call_deepseek(ubr_text)
            else:
                normalized_provider = "lanyi"
                insight = await self._call_lanyi(ubr_text)
        latency_ms = (time.perf_counter() - started) * 1000.0
        if not insight:
            return {
                "insight": "None Cloud",
                "status": "error",
                "model_route": f"cloud_{normalized_provider}",
                "cloud_called": True,
                "cloud_call_count": 1,
                "gate_would_defer": False,
                "cloud_latency_ms": latency_ms,
            }
        return {
            "insight": insight["insight"],
            "status": "completed",
            "model_route": f"cloud_{normalized_provider}",
            "cloud_called": True,
            "cloud_call_count": 1,
            "cloud_prompt_tokens": int(insight.get("prompt_tokens", 0) or 0),
            "cloud_completion_tokens": int(
                insight.get("completion_tokens", 0) or 0
            ),
            "cloud_cost_usd": insight.get("calculated_cost_usd"),
            "gate_would_defer": False,
            "cloud_latency_ms": latency_ms,
            "consensus_source": normalized_provider,
        }

    async def refine_insight_with_voting(self, full_ubr: dict, phase: str = "",
                                         fragment_seq: int = -1) -> Optional[Dict]:
        started = time.perf_counter()
        try:
            ubr_text = self._format_ubr_text(full_ubr, phase)
            async with self.cloud_semaphore:
                voted = await self._llm_voting_insight(full_ubr, ubr_text)
            if not voted:
                return None
            return {
                "insight": voted["insight"],
                "status": "completed",
                "consensus_score": voted.get("consensus_score"),
                "consensus_source": voted.get("llm_name"),
                "model_route": "cloud_voting_fallback",
                "cloud_called": True,
                "cloud_call_count": int(voted.get("cloud_call_count", 3) or 3),
                "cloud_prompt_tokens": int(
                    voted.get("cloud_prompt_tokens", 0) or 0
                ),
                "cloud_completion_tokens": int(
                    voted.get("cloud_completion_tokens", 0) or 0
                ),
                "cloud_cost_usd": voted.get("cloud_cost_usd"),
                "cloud_latency_ms": (time.perf_counter() - started) * 1000.0,
            }
        except Exception as e:
            logger.error(f"[Voting] error: {e}")
            return None

    async def get_voice_intent(self, text: str, seq: int) -> Optional[str]:
        if not text:
            return None
        await self.ensure_session()
        payload = {
            "model": config.LANYI_MODEL,
            "input": [
                {"role": "developer", "content": VOICE_INTENT_PROMPT},
                {"role": "user", "content": text}
            ],
            "max_output_tokens": 100,
            "text": {"verbosity": "low"},
        }
        try:
            async with self.session.post(
                config.LANYI_API_URL,
                headers={"Authorization": f"Bearer {config.LANYI_API_KEY}",
                         "Content-Type": "application/json"},
                json=payload,
                timeout=aiohttp.ClientTimeout(
                    total=config.CLOUD_REQUEST_TIMEOUT_SECONDS
                )
            ) as resp:
                if resp.status != 200:
                    return None
                result = await resp.json()
                return self._extract_responses_text(result) or None
        except Exception as e:
            logger.error(f"[Voice intent] seq={seq} error: {e}")
            return None

    def _threshold_check(self, logprobs: List[float]) -> bool:
        if not logprobs:
            return False
        return np.quantile(logprobs, self.alpha) >= self.threshold

    def _format_ubr_text(self, full_ubr: dict, phase: str = "") -> str:
        """Match the exact UBR input contract used by the 5,000-row training set."""
        return f"""User: {full_ubr.get('User', '')}
Action: {full_ubr.get('UserAction', '')}
Intent: {full_ubr.get('UserIntent', '')}
Trigger Source: {full_ubr.get('ActionTriggerSource', '')}
Referent Type: {full_ubr.get('ActionReferentType', '')}
Start Time: {full_ubr.get('Timestamp', '')}
Context: {full_ubr.get('ActionContextDescription', full_ubr.get('ActionContext', ''))}
ActionReferent:
  Object: {full_ubr.get('ActionReferentName', '')}
  Timestamp: {full_ubr.get('Timestamp', '')}
  Invoke Location: {full_ubr.get('Location', '')}
  Referent Location: {full_ubr.get('ActionReferentTransform', '')}"""

    async def _call_tiny_model(
        self, prompt: str, api_url: str, return_logprobs: bool = True
    ) -> tuple:
        await self.ensure_session()
        is_ollama = api_url.rstrip("/").endswith("/api/generate")
        if is_ollama:
            payload = {
                "model": config.OLLAMA_TINY_MODEL,
                "prompt": prompt,
                "stream": False,
                "think": False,
                "keep_alive": "30m",
                "logprobs": return_logprobs,
                "options": {
                    "temperature": 0,
                    "num_predict": 100,
                    "seed": 4090,
                },
            }
        else:
            payload = {
                "ubr": prompt,
                "max_new_tokens": 100,
                "return_logprobs": return_logprobs,
            }
        try:
            async with self.session.post(api_url, json=payload, timeout=aiohttp.ClientTimeout(total=15)) as resp:
                if resp.status != 200:
                    logger.info(f"[TinyModel] unavailable: HTTP {resp.status}, using None Tiny")
                    return "None Tiny", []
                result = await resp.json()
                if is_ollama:
                    text = str(result.get("response", "")).strip()
                    logprobs = [
                        float(item["logprob"])
                        for item in result.get("logprobs", [])
                        if (
                            isinstance(item, dict)
                            and item.get("logprob") is not None
                            and item.get("content", True) is not False
                        )
                    ]
                else:
                    text = str(result.get("insight", "")).strip()
                    logprobs = [
                        float(value)
                        for value in result.get("content_token_logprobs", [])
                    ]
                return text, logprobs
        except Exception as e:
            logger.info(f"[TinyModel] unavailable, using None Tiny: {e}")
            return "None Tiny", []

    async def _call_qwen(self, ubr_text: str) -> Optional[Dict]:
        await self.ensure_session()
        payload = {
            "model": config.QWEN_MODEL,
            "messages": [{"role": "system", "content": INSIGHT_SYSTEM_PROMPT},
                         {"role": "user", "content": ubr_text}],
            "max_tokens": 150, "temperature": 0
        }
        try:
            async with self.session.post(
                config.QWEN_API_URL,
                headers={"Authorization": f"Bearer {config.QWEN_API_KEY}",
                         "Content-Type": "application/json"},
                json=payload,
                timeout=aiohttp.ClientTimeout(
                    total=config.CLOUD_REQUEST_TIMEOUT_SECONDS
                )
            ) as resp:
                if resp.status != 200:
                    logger.error("[Qwen] HTTP %s: %s", resp.status, await resp.text())
                    return None
                result = await resp.json()
                parsed = self._parse_openai_response(result)
                return self._attach_cost(parsed, "qwen")
        except Exception as e:
            logger.error(f"[Qwen] error: {e}")
            return None

    async def _call_deepseek(self, ubr_text: str) -> Optional[Dict]:
        await self.ensure_session()
        payload = {
            "model": config.DEEPSEEK_MODEL,
            "messages": [{"role": "system", "content": INSIGHT_SYSTEM_PROMPT},
                         {"role": "user", "content": ubr_text}],
            "max_tokens": 150, "temperature": 0
        }
        try:
            async with self.session.post(
                config.DEEPSEEK_API_URL,
                headers={"Authorization": f"Bearer {config.DEEPSEEK_API_KEY}",
                         "Content-Type": "application/json"},
                json=payload,
                timeout=aiohttp.ClientTimeout(
                    total=config.CLOUD_REQUEST_TIMEOUT_SECONDS
                )
            ) as resp:
                if resp.status != 200:
                    logger.error(
                        "[DeepSeek] HTTP %s: %s", resp.status, await resp.text()
                    )
                    return None
                result = await resp.json()
                parsed = self._parse_openai_response(result)
                return self._attach_cost(parsed, "deepseek")
        except Exception as e:
            logger.error(f"[DeepSeek] error: {e}")
            return None

    async def _call_lanyi(self, ubr_text: str) -> Optional[Dict]:
        await self.ensure_session()
        payload = {
            "model": config.LANYI_MODEL,
            "input": [
                {"role": "developer", "content": INSIGHT_SYSTEM_PROMPT},
                {"role": "user", "content": ubr_text},
            ],
            "max_output_tokens": 150,
            "text": {"verbosity": "low"},
        }
        try:
            async with self.session.post(
                config.LANYI_API_URL,
                headers={"Authorization": f"Bearer {config.LANYI_API_KEY}",
                         "Content-Type": "application/json"},
                json=payload,
                timeout=aiohttp.ClientTimeout(
                    total=config.CLOUD_REQUEST_TIMEOUT_SECONDS
                )
            ) as resp:
                if resp.status != 200:
                    logger.error("[Lanyi] HTTP %s: %s", resp.status, await resp.text())
                    return None
                result = await resp.json()
                parsed = self._parse_responses_api_response(result)
                return self._attach_cost(parsed, "lanyi")
        except Exception as e:
            logger.error(f"[Lanyi] error: {e}")
            return None

    async def _llm_voting_insight(self, full_ubr: dict, ubr_text: str) -> Optional[Dict]:
        try:
            self._ensure_voting_system()
            tasks = [
                (self._call_qwen(ubr_text), "Qwen"),
                (self._call_deepseek(ubr_text), "DeepSeek"),
                (self._call_lanyi(ubr_text), "Lanyi")
            ]
            results = await asyncio.gather(*[t for t, _ in tasks], return_exceptions=True)
            insights, names = [], []
            total_prompt_tokens = 0
            total_completion_tokens = 0
            costs: list[float] = []
            successful_calls = 0
            for result, (_, name) in zip(results, tasks):
                if result and not isinstance(result, Exception):
                    if isinstance(result, dict) and "insight" in result:
                        insights.append(result)
                    else:
                        insights.append({"topic": "UBR Insight", "insight": str(result)})
                    names.append(name)
                    successful_calls += 1
                    total_prompt_tokens += int(result.get("prompt_tokens", 0) or 0)
                    total_completion_tokens += int(
                        result.get("completion_tokens", 0) or 0
                    )
                    if result.get("calculated_cost_usd") is not None:
                        costs.append(float(result["calculated_cost_usd"]))
            if not insights:
                return None
            best = self.voting_system.vote(insights, llm_names=names)
            best["cloud_call_count"] = successful_calls
            best["cloud_prompt_tokens"] = total_prompt_tokens
            best["cloud_completion_tokens"] = total_completion_tokens
            best["cloud_cost_usd"] = (
                sum(costs) if len(costs) == successful_calls else None
            )
            return best
        except Exception as e:
            logger.error(f"[Voting] error: {e}")
            return None

    @staticmethod
    def _parse_openai_response(result: dict) -> Optional[Dict]:
        try:
            insight = result["choices"][0]["message"]["content"].strip()
            try:
                parsed = json.loads(insight)
                if "topic" in parsed and "insight" in parsed:
                    parsed["prompt_tokens"] = int(
                        result.get("usage", {}).get("prompt_tokens", 0) or 0
                    )
                    parsed["completion_tokens"] = int(
                        result.get("usage", {}).get("completion_tokens", 0) or 0
                    )
                    return parsed
            except json.JSONDecodeError:
                pass
            return {
                "topic": "UBR Insight",
                "insight": insight,
                "prompt_tokens": int(
                    result.get("usage", {}).get("prompt_tokens", 0) or 0
                ),
                "completion_tokens": int(
                    result.get("usage", {}).get("completion_tokens", 0) or 0
                ),
            }
        except (KeyError, IndexError):
            return None

    @staticmethod
    def _extract_responses_text(result: dict) -> str:
        output_text = result.get("output_text")
        if isinstance(output_text, str):
            return output_text.strip()
        chunks = []
        for item in result.get("output", []) or []:
            if not isinstance(item, dict):
                continue
            for content in item.get("content", []) or []:
                if not isinstance(content, dict):
                    continue
                text = content.get("text")
                if isinstance(text, str):
                    chunks.append(text)
        return "".join(chunks).strip()

    @classmethod
    def _parse_responses_api_response(cls, result: dict) -> Optional[Dict]:
        try:
            text = cls._extract_responses_text(result)
            if not text:
                return None
            usage = result.get("usage", {})
            prompt_tokens = int(usage.get("input_tokens", 0) or 0)
            completion_tokens = int(usage.get("output_tokens", 0) or 0)
            try:
                parsed = json.loads(text)
                if "topic" in parsed and "insight" in parsed:
                    parsed["prompt_tokens"] = prompt_tokens
                    parsed["completion_tokens"] = completion_tokens
                    return parsed
            except json.JSONDecodeError:
                pass
            return {
                "topic": "UBR Insight",
                "insight": text,
                "prompt_tokens": prompt_tokens,
                "completion_tokens": completion_tokens,
            }
        except (KeyError, IndexError, TypeError, ValueError):
            return None

    def _ensure_voting_system(self) -> None:
        if self.voting_system is not None:
            return
        from llm_voting import SemanticEntropyVoting
        import models as voting_models
        voting_models.init_embeddings_model()
        self.voting_system = SemanticEntropyVoting(
            embeddings_model=voting_models.embeddings_model,
            tokenizer=voting_models.tokenizer,
        )

    @staticmethod
    def _attach_cost(result: Optional[Dict], provider: str) -> Optional[Dict]:
        if not result:
            return result
        pricing = config.CLOUD_PRICING_USD_PER_1M.get(provider, {})
        input_rate = float(pricing.get("input", 0) or 0)
        output_rate = float(pricing.get("output", 0) or 0)
        result["provider"] = provider
        if input_rate <= 0 and output_rate <= 0:
            result["calculated_cost_usd"] = None
        else:
            result["calculated_cost_usd"] = (
                int(result.get("prompt_tokens", 0) or 0) * input_rate
                + int(result.get("completion_tokens", 0) or 0) * output_rate
            ) / 1_000_000
        return result
