"""LLM voting system with MBR-style semantic consensus."""

import numpy as np
import torch
from typing import Dict, List, Optional
from sklearn.metrics.pairwise import cosine_similarity
import logging

logger = logging.getLogger(__name__)


class SemanticEntropyVoting:
    """MBR-style LLM voting system.

    The class name is kept for compatibility with the current imports.
    """

    def __init__(self, embeddings_model=None, tokenizer=None,
                 temperature: float = 1.0, sigma_sq_max: float = 0.25, epsilon: float = 1e-6):
        if embeddings_model is None or tokenizer is None:
            raise ValueError("embeddings_model and tokenizer are required")
        self.embeddings_model = embeddings_model
        self.tokenizer = tokenizer
        self.temperature = temperature
        self.sigma_sq_max = sigma_sq_max
        self.epsilon = epsilon
        self.device = next(embeddings_model.parameters()).device

    def compute_embeddings(self, insights: List[Dict]) -> np.ndarray:
        """Compute semantic embeddings for insights"""
        texts = [f"{ins['topic']} {ins['insight']}" for ins in insights]
        encoded = self.tokenizer(texts, padding=True, truncation=True, max_length=512, return_tensors='pt')
        encoded = {k: v.to(self.device) for k, v in encoded.items()}
        with torch.no_grad():
            output = self.embeddings_model(**encoded)
        token_emb = output.last_hidden_state
        mask = encoded['attention_mask'].unsqueeze(-1).expand(token_emb.size()).float()
        embeddings = torch.sum(token_emb * mask, dim=1) / torch.clamp(mask.sum(dim=1), min=1e-9)
        return embeddings.cpu().numpy()

    def vote(self, llm_insights: List[Dict], llm_names: Optional[List[str]] = None) -> Dict:
        """Vote for the best insight using average pairwise semantic utility."""
        if not llm_insights:
            raise ValueError("At least one LLM insight is required")

        n = len(llm_insights)
        if llm_names is None:
            llm_names = [f"LLM_{i+1}" for i in range(n)]

        embeddings = self.compute_embeddings(llm_insights)
        similarity_matrix = cosine_similarity(embeddings)
        if n == 1:
            consensus_scores = np.ones(1, dtype=float)
        else:
            consensus_scores = (np.sum(similarity_matrix, axis=1) - 1.0) / (n - 1)

        results = []
        for i, (insight, name) in enumerate(zip(llm_insights, llm_names)):
            results.append({
                **insight, "llm_name": name,
                "average_pairwise_similarity": float(consensus_scores[i]),
                "similarity_to_center": float(consensus_scores[i]),
                "consensus_score": float(consensus_scores[i]),
                "pairwise_similarities": [
                    float(similarity_matrix[i, j]) for j in range(n) if j != i
                ],
            })

        best = max(results, key=lambda x: x['consensus_score'])
        logger.info(f"[Voting] Best: {best['llm_name']} (consensus={best['consensus_score']:.3f})")
        return best
