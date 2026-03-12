import os

WS_HOST = "0.0.0.0"
WS_PORT = 8765

LOG_QUEUE_SIZE = 1000
CONTEXT_QUEUE_SIZE = 100
HAND_QUEUE_SIZE = 100
VOICE_QUEUE_SIZE = 100

SAVE_DIR = "./received_data"
OUTPUT_DIR = "./output"

OPENAI_API_KEY = os.environ.get("OPENAI_API_KEY", "")
QWEN_API_KEY = os.environ.get("QWEN_API_KEY", "")
GEMINI_API_KEY = os.environ.get("GEMINI_API_KEY", "")

WHISPER_MODEL = "base"

IMAGE2TEXT_API_URL = "http://localhost:8001/predict"
LLM_INSIGHT_API_URL = "http://localhost:8000/v1/chat/completions"
OPENAI_API_URL = "https://api.openai.com/v1/chat/completions"
QWEN_API_URL = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"
GEMINI_API_URL = "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent"

YOLO_MODEL_PATH = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "yolo26n.pt")
YOLO_CONF_THRESHOLD = 0.5
YOLO_IMGSZ = 480
YOLO_DEVICE = "cuda:0"

POINTCLOUD_DOWNSAMPLE = 2
DEPTH_MIN = 0.1
DEPTH_MAX = 5.0

MAX_CONCURRENT_LLM_CALLS = 5
MAX_CONCURRENT_GLB_TASKS = 3
ENABLE_DISK_SAVE = True

LOG_LEVEL = "INFO"
LOG_FORMAT = "%(asctime)s - %(name)s - %(levelname)s - %(message)s"

MAX_MESSAGE_SIZE = 100 * 1024 * 1024
