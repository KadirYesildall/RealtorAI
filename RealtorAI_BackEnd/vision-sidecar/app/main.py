"""Vision sidecar for AI Listing Launch — T010, T093, T094.

Contract: specs/002-ai-listing-launch/contracts/vision-sidecar.md

Boundary rules enforced here:

* **No generative capability** (Constitution VIII.6). This service has no image- or
  video-generation dependency and no endpoint that produces pixels. It only measures and
  embeds. The prohibition test in tests/prohibitions scans for violations.
* **Normalized copies only.** Callers send the normalized variant (long edge ~1280px,
  EXIF/GPS stripped). Originals never leave the write-once bucket (FR-001).
* **Stateless.** Nothing is persisted; the API stores every result.
* **Internal only.** Called by the .NET API, never by a browser.
"""

from __future__ import annotations

import logging

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import JSONResponse

from . import embed as embedding
from . import quality

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(
    title="RealtorAI Vision Sidecar",
    version="0.1.0",
    description=(
        "Deterministic image quality metrics and image embeddings. No generative capability "
        "(Constitution VIII.6)."
    ),
)

MAX_UPLOAD_BYTES = 50 * 1024 * 1024  # matches Supabase Storage's 50 MiB bucket limit


@app.get("/health")
async def health() -> JSONResponse:
    """Readiness for Compose/orchestration.

    Reports 200 once the service can serve /quality, which needs no model. `model_loaded`
    tells the caller whether /embed is currently usable, so a disabled or unverified
    embedding model shows up as a capability gap rather than an outage.
    """
    return JSONResponse(
        status_code=200,
        content={
            "status": "ok",
            "model_loaded": embedding.is_loaded(),
            "model_load_error": embedding.load_error(),
        },
    )


async def _read_upload(file: UploadFile) -> bytes:
    payload = await file.read()
    if not payload:
        raise HTTPException(status_code=422, detail="empty upload")
    if len(payload) > MAX_UPLOAD_BYTES:
        raise HTTPException(
            status_code=413,
            detail=f"upload exceeds {MAX_UPLOAD_BYTES} bytes",
        )
    return payload


@app.post("/quality")
async def post_quality(
    file: UploadFile = File(...),
    min_width: int = Form(...),
    min_height: int = Form(...),
) -> JSONResponse:
    """Deterministic technical-quality metrics. No model, so no VIII.4 gate applies."""
    payload = await _read_upload(file)

    try:
        result = quality.analyse(payload, min_width=min_width, min_height=min_height)
    except ValueError as exc:
        # Unsupported or corrupt image: the caller's file is at fault, not this service.
        raise HTTPException(status_code=415, detail=str(exc)) from exc

    return JSONResponse(
        status_code=200,
        content={
            "width": result.width,
            "height": result.height,
            "blur_score": result.blur_score,
            "mean_luminance": result.mean_luminance,
            "clipped_highlight_ratio": result.clipped_highlight_ratio,
            "clipped_shadow_ratio": result.clipped_shadow_ratio,
            "horizon_tilt_degrees": result.horizon_tilt_degrees,
            "flags": result.flags,
        },
    )


@app.post("/embed")
async def post_embed(
    file: UploadFile = File(...),
    model_id: str = Form(...),
) -> JSONResponse:
    """Image embedding for near-duplicate detection.

    `model_id` comes from the API's `ai_model_registry` lookup — this service never chooses a
    model. The returned `model_id` is what was actually loaded, so the audit log records
    reality (FR-049).

    Returns 503 when weights are unavailable, which the pipeline treats as "near-duplicate
    detection unavailable" rather than a listing failure (SC-010).
    """
    payload = await _read_upload(file)

    try:
        vector, dimension, loaded_model_id = embedding.embed(payload, model_id)
    except embedding.ModelUnavailableError as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    except ValueError as exc:
        raise HTTPException(status_code=415, detail=str(exc)) from exc

    return JSONResponse(
        status_code=200,
        content={
            "model_id": loaded_model_id,
            "dimension": dimension,
            "embedding": vector,
        },
    )
