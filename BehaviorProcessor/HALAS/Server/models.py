tokenizer = None
embeddings_model = None

def init_embeddings_model():
    """Load only the semantic-voting encoder used by cloud fallback."""
    from transformers import AutoTokenizer, AutoModel
    import warnings
    import torch
    warnings.filterwarnings("ignore", category=FutureWarning, module="huggingface_hub")
    global tokenizer, embeddings_model
    if tokenizer is not None and embeddings_model is not None:
        return
    device = "cuda" if torch.cuda.is_available() else "cpu"
    embeddings_model_name = "colbert-ir/colbertv2.0"
    tokenizer = AutoTokenizer.from_pretrained(embeddings_model_name)
    embeddings_model = AutoModel.from_pretrained(embeddings_model_name)
    embeddings_model = embeddings_model.to(device).eval()
