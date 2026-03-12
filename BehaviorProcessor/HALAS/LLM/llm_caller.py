import asyncio
import aiohttp
import numpy as np
import sys
from pathlib import Path
from typing import Optional, List, Dict
import logging
import json

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "Server"))
import config

logger = logging.getLogger(__name__)

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

    async def ensure_session(self):
        if self.session is None:
            self.connector = aiohttp.TCPConnector(limit=20, ttl_dns_cache=300)
            self.session = aiohttp.ClientSession(connector=self.connector)

    async def close(self):
        if self.session:
            await self.session.close()
        if self.connector:
            await self.connector.close()

    async def get_context_description(self, image_data: bytes, seq: int) -> Optional[str]:
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

    async def generate_ubr_insight(self, full_ubr: dict) -> Optional[Dict]:
        try:
            ubr_text = self._format_ubr_text(full_ubr)
            insight, logprobs = await self._call_tiny_model(ubr_text, config.LLM_INSIGHT_API_URL)
            if not insight:
                return None
            status = "completed" if self._threshold_check(logprobs) else "pending"
            return {"insight": insight, "status": status}
        except Exception as e:
            logger.error(f"[Insight] error: {e}")
            return None

    async def refine_insight_with_voting(self, full_ubr: dict) -> Optional[Dict]:
        try:
            ubr_text = self._format_ubr_text(full_ubr)
            insight = await self._llm_voting_insight(full_ubr, ubr_text)
            return {"insight": insight, "status": "completed"} if insight else None
        except Exception as e:
            logger.error(f"[Voting] error: {e}")
            return None

    async def get_voice_intent(self, text: str, seq: int) -> Optional[str]:
        if not text:
            return None
        await self.ensure_session()
        payload = {
            "model": "gpt-4.1-nano",
            "messages": [
                {"role": "system", "content": VOICE_INTENT_PROMPT},
                {"role": "user", "content": text}
            ],
            "temperature": 0, "max_tokens": 100
        }
        try:
            async with self.session.post(
                config.OPENAI_API_URL,
                headers={"Authorization": f"Bearer {config.OPENAI_API_KEY}",
                         "Content-Type": "application/json"},
                json=payload, timeout=aiohttp.ClientTimeout(total=15)
            ) as resp:
                if resp.status != 200:
                    return None
                result = await resp.json()
                return result["choices"][0]["message"]["content"].strip()
        except Exception as e:
            logger.error(f"[Voice intent] seq={seq} error: {e}")
            return None

    def _threshold_check(self, logprobs: List[float]) -> bool:
        if not logprobs:
            return False
        return np.quantile(logprobs, self.alpha) >= self.threshold

    def _format_ubr_text(self, full_ubr: dict) -> str:
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

    async def _call_tiny_model(self, prompt: str, api_url: str) -> tuple:
        await self.ensure_session()
        payload = {
            "model": "./uadtiny/s/minimind/output_xr/final",
            "messages": [
                {"role": "user", "content": prompt}
            ],
            "stream": False, "temperature": 0.05, "top_p": 1,
            "max_tokens": 100, "logprobs": True, "top_logprobs": 1
        }
        try:
            async with self.session.post(api_url, json=payload, timeout=aiohttp.ClientTimeout(total=15)) as resp:
                if resp.status != 200:
                    return None, []
                result = await resp.json()
                text = result["choices"][0]["message"]["content"].strip()
                logprobs = []
                lp_data = result["choices"][0].get("logprobs", {})
                if lp_data and "content" in lp_data:
                    logprobs = [t["logprob"] for t in lp_data["content"]]
                return text, logprobs
        except Exception as e:
            logger.error(f"[TinyModel] error: {e}")
            return None, []

    async def _call_qwen(self, ubr_text: str) -> Optional[Dict]:
        await self.ensure_session()
        payload = {
            "model": "qwen-plus",
            "messages": [{"role": "system", "content": INSIGHT_SYSTEM_PROMPT},
                         {"role": "user", "content": ubr_text}],
            "max_tokens": 150, "temperature": 0.7
        }
        try:
            async with self.session.post(
                config.QWEN_API_URL,
                headers={"Authorization": f"Bearer {config.QWEN_API_KEY}",
                         "Content-Type": "application/json"},
                json=payload, timeout=aiohttp.ClientTimeout(total=15)
            ) as resp:
                if resp.status != 200:
                    return None
                result = await resp.json()
                return self._parse_openai_response(result)
        except Exception as e:
            logger.error(f"[Qwen] error: {e}")
            return None

    async def _call_openai(self, ubr_text: str) -> Optional[Dict]:
        await self.ensure_session()
        payload = {
            "model": "gpt-4o-mini",
            "messages": [{"role": "system", "content": INSIGHT_SYSTEM_PROMPT},
                         {"role": "user", "content": ubr_text}],
            "max_tokens": 150, "temperature": 0.7
        }
        try:
            async with self.session.post(
                config.OPENAI_API_URL,
                headers={"Authorization": f"Bearer {config.OPENAI_API_KEY}",
                         "Content-Type": "application/json"},
                json=payload, timeout=aiohttp.ClientTimeout(total=15)
            ) as resp:
                if resp.status != 200:
                    return None
                result = await resp.json()
                return self._parse_openai_response(result)
        except Exception as e:
            logger.error(f"[OpenAI] error: {e}")
            return None

    async def _call_gemini(self, ubr_text: str) -> Optional[Dict]:
        await self.ensure_session()
        payload = {
            "contents": [
                {"role": "user", "parts": [{"text": ubr_text}]}
            ],
            "systemInstruction": {
                "parts": [{"text": INSIGHT_SYSTEM_PROMPT}]
            },
            "generationConfig": {
                "maxOutputTokens": 150,
                "temperature": 0.7
            }
        }
        try:
            async with self.session.post(
                f"{config.GEMINI_API_URL}?key={config.GEMINI_API_KEY}",
                headers={"Content-Type": "application/json"},
                json=payload, timeout=aiohttp.ClientTimeout(total=15)
            ) as resp:
                if resp.status != 200:
                    return None
                result = await resp.json()
                return self._parse_gemini_response(result)
        except Exception as e:
            logger.error(f"[Gemini] error: {e}")
            return None

    async def _llm_voting_insight(self, full_ubr: dict, ubr_text: str) -> Optional[str]:
        try:
            tasks = [
                (self._call_qwen(ubr_text), "Qwen"),
                (self._call_openai(ubr_text), "OpenAI"),
                (self._call_gemini(ubr_text), "Gemini")
            ]
            results = await asyncio.gather(*[t for t, _ in tasks], return_exceptions=True)
            insights, names = [], []
            for result, (_, name) in zip(results, tasks):
                if result and not isinstance(result, Exception):
                    if isinstance(result, dict) and "insight" in result:
                        insights.append(result)
                    else:
                        insights.append({"topic": "UBR Insight", "insight": str(result)})
                    names.append(name)
            if not insights:
                return None
            best = self.voting_system.vote(insights, llm_names=names)
            return best["insight"]
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
                    return parsed
            except json.JSONDecodeError:
                pass
            return {"topic": "UBR Insight", "insight": insight}
        except (KeyError, IndexError):
            return None

    @staticmethod
    def _parse_gemini_response(result: dict) -> Optional[Dict]:
        try:
            text = result["candidates"][0]["content"]["parts"][0]["text"].strip()
            try:
                parsed = json.loads(text)
                if "topic" in parsed and "insight" in parsed:
                    return parsed
            except json.JSONDecodeError:
                pass
            return {"topic": "UBR Insight", "insight": text}
        except (KeyError, IndexError):
            return None
