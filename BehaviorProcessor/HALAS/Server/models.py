import config

open_ai_client = None
whisper_model = None
tokenizer = None
embeddings_model = None

def init_models():
    try:
        from openai import OpenAI
        from transformers import AutoTokenizer, AutoModel
        import whisper
        import warnings
        import torch
        warnings.filterwarnings("ignore", category=FutureWarning, module="huggingface_hub")
        global open_ai_client, whisper_model, tokenizer, embeddings_model
        device = "cuda" if torch.cuda.is_available() else "cpu"
        open_ai_client = OpenAI(api_key=config.OPENAI_API_KEY)
        whisper_model = whisper.load_model("base")
        embeddings_model_name = "colbert-ir/colbertv2.0"
        tokenizer = AutoTokenizer.from_pretrained(embeddings_model_name)
        embeddings_model = AutoModel.from_pretrained(embeddings_model_name)
        embeddings_model = embeddings_model.to(device)
        _ = embeddings_model.eval()
    except Exception as e:
        print(f"Error initializing models ({e})")
        return