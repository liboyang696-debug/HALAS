import os
from pathlib import Path

from dotenv import load_dotenv

PROCESSOR_ROOT = Path(__file__).resolve().parents[2]
REPOSITORY_ROOT = PROCESSOR_ROOT.parent
load_dotenv(PROCESSOR_ROOT / ".env", override=False)

WS_HOST = os.environ.get("HALAS_WS_HOST", "0.0.0.0")
WS_PORT = int(os.environ.get("HALAS_WS_PORT", "8765"))
FRONTEND_PORT = int(os.environ.get("HALAS_FRONTEND_PORT", "8766"))
WEBAPP_ROOT = Path(os.environ.get(
    "HALAS_WEBAPP_ROOT", str(REPOSITORY_ROOT / "Visual Analytics Interface" / "webApp")
)).expanduser().resolve()
REFERENCE_DIR = Path(os.environ.get(
    "HALAS_REFERENCE_DIR", str(PROCESSOR_ROOT / "HALAS" / "Reference")
)).expanduser().resolve()

LOG_QUEUE_SIZE = 1000
CONTEXT_QUEUE_SIZE = 100
HAND_QUEUE_SIZE = 100
VOICE_QUEUE_SIZE = 100

SAVE_DIR = "./received_data"
OUTPUT_DIR = "./output"

# Credentials are supplied by the deployment environment.
DEEPSEEK_API_KEY = os.environ.get("DEEPSEEK_API_KEY", "")
QWEN_API_KEY = os.environ.get("QWEN_API_KEY", "")
LANYI_API_KEY = os.environ.get("LANYI_API_KEY", "")

WHISPER_MODEL = "base"

ENABLE_IMAGE2TEXT_MODEL = os.environ.get("HALAS_ENABLE_IMAGE2TEXT", "0").lower() in ("1", "true", "yes", "on")
ENABLE_TINY_MODEL = os.environ.get("HALAS_ENABLE_TINY_MODEL", "0").lower() in ("1", "true", "yes", "on")
BASELINE_MODE = os.environ.get("HALAS_BASELINE_MODE", "full_halas")
BASELINE_CONFIG_PATH = os.environ.get("HALAS_BASELINE_CONFIG_PATH", "")
BASELINE_METRICS_DIR = os.environ.get("HALAS_BASELINE_METRICS_DIR", "")
NAIVE_CLOUD_PROVIDER = os.environ.get("HALAS_NAIVE_CLOUD_PROVIDER", "lanyi").lower()
# The gate consumes content-token log probabilities; calibrate it with the deployed model.
TOKEN_CONFIDENCE_THRESHOLD = float(
    os.environ.get("HALAS_TOKEN_THRESHOLD", "-0.084277861379087")
)
TOKEN_CONFIDENCE_ALPHA = float(os.environ.get("HALAS_TOKEN_ALPHA", "0.3"))

CLOUD_PRICING_USD_PER_1M = {
    "lanyi": {
        "input": float(os.environ.get("HALAS_LANYI_INPUT_USD_PER_1M", "0")),
        "output": float(os.environ.get("HALAS_LANYI_OUTPUT_USD_PER_1M", "0")),
    },
    "qwen": {
        "input": float(os.environ.get("HALAS_QWEN_INPUT_USD_PER_1M", "0")),
        "output": float(os.environ.get("HALAS_QWEN_OUTPUT_USD_PER_1M", "0")),
    },
    "deepseek": {
        "input": float(os.environ.get("HALAS_DEEPSEEK_INPUT_USD_PER_1M", "0")),
        "output": float(os.environ.get("HALAS_DEEPSEEK_OUTPUT_USD_PER_1M", "0")),
    },
}

IMAGE2TEXT_API_URL = os.environ.get(
    "HALAS_IMAGE2TEXT_API_URL",
    "http://127.0.0.1:8000/caption",
)
LLM_INSIGHT_API_URL = os.environ.get(
    "HALAS_TINY_MODEL_API_URL",
    "http://127.0.0.1:8000/insight",
)
OLLAMA_TINY_MODEL = os.environ.get("HALAS_OLLAMA_TINY_MODEL", "halas-minimind3")
QWEN_API_URL = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"
DEEPSEEK_API_URL = "https://api.deepseek.com/chat/completions"
LANYI_API_URL = "https://lanyiapi.com/v1/responses"
QWEN_MODEL = "qwen-plus"
DEEPSEEK_MODEL = "deepseek-chat"
LANYI_MODEL = "gpt-5.5"
CLOUD_REQUEST_TIMEOUT_SECONDS = 60

YOLO_MODEL_PATH = os.environ.get("HALAS_YOLO_MODEL_PATH", str(PROCESSOR_ROOT / "models" / "yolo26n.pt"))
YOLO_CONF_THRESHOLD = 0.5
YOLO_IMGSZ = 480
YOLO_DEVICE = os.environ.get("HALAS_YOLO_DEVICE", "cuda:0")

POINTCLOUD_DOWNSAMPLE = 2
DEPTH_MIN = 0.1
DEPTH_MAX = 5.0
MAX_CONTEXT_GLB_FILES = int(os.environ.get("HALAS_MAX_CONTEXT_GLB_FILES", "12"))

MAX_CONCURRENT_LLM_CALLS = 5
MAX_CONCURRENT_GLB_TASKS = 3
ENABLE_DISK_SAVE = True

LOG_LEVEL = "INFO"
LOG_FORMAT = "%(asctime)s - %(name)s - %(levelname)s - %(message)s"

MAX_MESSAGE_SIZE = 100 * 1024 * 1024
