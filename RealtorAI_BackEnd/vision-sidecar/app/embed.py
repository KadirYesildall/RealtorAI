"""Image embeddings for near-duplicate detection and zero-shot room pre-classification.

Contract: specs/002-ai-listing-launch/contracts/vision-sidecar.md

Two deliberate design points:

1. **Lazy loading with an honest 503.** The model weights are NOT loaded at import time. The
   `photo.embedding` registry entry is still `enabled = false` / `eval_status = 'unverified'`
   (Principle VIII.4), so the model has not been cleared for use and the heavy ML stack is not
   yet a hard install requirement. Until it is, `/embed` reports 503 exactly as the contract
   specifies, and the pipeline degrades to "near-duplicate detection unavailable" without
   failing the listing.

2. **The sidecar does not decide which model to use.** The API passes the model id resolved
   from `ai_model_registry` and the sidecar echoes back what it actually loaded, so the audit
   log records reality rather than intent (FR-049). A sidecar that hard-coded its own model id
   as authority would make the registry a lie.
"""

from __future__ import annotations

import io
import logging
import threading

logger = logging.getLogger(__name__)

# Guards the one-time load so concurrent first requests cannot race into loading twice.
_load_lock = threading.Lock()
_model = None
_processor = None
_loaded_model_id: str | None = None
_load_error: str | None = None


class ModelUnavailableError(RuntimeError):
    """Raised when embedding weights are not loadable in this environment."""


def _try_load(model_id: str) -> None:
    """Attempt a one-time model load. Records the failure reason instead of raising at import."""
    global _model, _processor, _loaded_model_id, _load_error

    if _loaded_model_id == model_id and _model is not None:
        return

    with _load_lock:
        if _loaded_model_id == model_id and _model is not None:
            return
        try:
            # Imported lazily so the sidecar starts — and serves /quality — without the ML
            # stack installed.
            import torch  # noqa: F401
            from transformers import AutoModel, AutoProcessor

            logger.info("loading embedding model %s", model_id)
            _processor = AutoProcessor.from_pretrained(model_id)
            _model = AutoModel.from_pretrained(model_id)
            _model.eval()
            _loaded_model_id = model_id
            _load_error = None
        except Exception as exc:  # noqa: BLE001 - reported to the caller as 503
            _load_error = f"{type(exc).__name__}: {exc}"
            _model = None
            _processor = None
            _loaded_model_id = None
            logger.warning("embedding model unavailable: %s", _load_error)


def is_loaded() -> bool:
    return _model is not None


def load_error() -> str | None:
    return _load_error


def embed(image_bytes: bytes, model_id: str) -> tuple[list[float], int, str]:
    """Return (embedding, dimension, loaded_model_id).

    Raises ModelUnavailableError when weights cannot be loaded, which the route maps to 503.
    """
    _try_load(model_id)
    if _model is None or _processor is None:
        raise ModelUnavailableError(
            _load_error
            or "embedding model not loaded; install the ML dependencies and verify the "
               "registry entry first"
        )

    import torch
    from PIL import Image

    image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    inputs = _processor(images=image, return_tensors="pt")

    with torch.no_grad():
        features = _model.get_image_features(**inputs)
        # L2-normalise so cosine similarity is a plain dot product, matching the pgvector
        # vector_cosine_ops index the API queries.
        features = features / features.norm(p=2, dim=-1, keepdim=True)

    vector = features[0].tolist()
    return vector, len(vector), _loaded_model_id or model_id
