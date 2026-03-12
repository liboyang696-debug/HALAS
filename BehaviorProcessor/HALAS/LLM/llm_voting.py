"""LLM Voting System with Semantic Entropy and Bayesian Variance"""

import json
import numpy as np
import torch
from typing import Dict, List, Optional
from sklearn.metrics.pairwise import cosine_similarity
import logging

logger = logging.getLogger(__name__)


class SemanticEntropyVoting:
    """Semantic entropy-based LLM voting system"""

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
        """Vote for the best insight based on semantic similarity confidence"""
        if not llm_insights:
            raise ValueError("At least one LLM insight is required")

        n = len(llm_insights)
        if llm_names is None:
            llm_names = [f"LLM_{i+1}" for i in range(n)]

        embeddings = self.compute_embeddings(llm_insights)
        centroid = np.mean(embeddings, axis=0, keepdims=True)
        similarities = cosine_similarity(embeddings, centroid).flatten()

        # Softmax probability distribution
        exp_sim = np.exp(similarities / self.temperature)
        probabilities = exp_sim / np.sum(exp_sim)

        # Semantic entropy
        p_safe = np.clip(probabilities, self.epsilon, 1.0)
        se = -np.sum(probabilities * np.log(p_safe))
        se_norm = se / (np.log(n) + self.epsilon)
        var_b = self.sigma_sq_max * se_norm

        results = []
        for i, (insight, name) in enumerate(zip(llm_insights, llm_names)):
            results.append({
                **insight, "llm_name": name,
                "similarity_to_center": float(similarities[i]),
                "probability": float(probabilities[i]),
                "semantic_entropy": float(se),
                "semantic_entropy_normalized": float(se_norm),
                "bayesian_variance": float(var_b),
                "bayesian_std": float(np.sqrt(var_b)),
                "confidence_score": float(probabilities[i])
            })

        best = max(results, key=lambda x: x['confidence_score'])
        logger.info(f"[Voting] Best: {best['llm_name']} (confidence={best['confidence_score']:.3f})")
        return best
