# Contract: Vision Sidecar

**Service**: `RealtorAI_BackEnd/vision-sidecar/` — Python 3.12 + FastAPI
**Consumed by**: `RealtorAI.Api` only. **Never** reachable from the browser.
**Status**: adopted as the plan-input default; an in-process .NET alternative (ONNX Runtime +
ImageSharp) is recommended as a Phase 0 spike — see
[research R13](../research.md#r13-vision-sidecar-versus-in-process-net) and plan.md Complexity
Tracking. This contract is written so either implementation satisfies it.

## Boundary rules

1. **No generative capability.** The sidecar MUST NOT include any image- or video-generation
   dependency or endpoint (Constitution VIII.6). The build-failing prohibition test covers this
   service as well as the API.
2. **Normalized copies only.** It receives the normalized variant (long edge ~1280px, EXIF/GPS
   stripped), never the original. Originals never leave the write-once bucket (FR-001).
3. **No persistence.** The sidecar is stateless; all results are stored by the API.
4. **Network isolation.** Bound to the internal Compose network with no published host port in
   deployment; a local port is exposed only for development.

## `POST /quality`

Deterministic technical-quality metrics. **No model** — not subject to the VIII.4 model gates.

**Request** (`multipart/form-data`): `file` (image), plus `min_width` and `min_height` integers
from the active MLS rule profile.

**Response** `200`:

```json
{
  "width": 4032,
  "height": 3024,
  "blur_score": 142.7,
  "mean_luminance": 0.46,
  "clipped_highlight_ratio": 0.012,
  "clipped_shadow_ratio": 0.004,
  "horizon_tilt_degrees": 3.8,
  "flags": ["tilted_horizon"]
}
```

| Field | Method | Flag raised when |
|---|---|---|
| `blur_score` | Variance of Laplacian | below the configured threshold → `blurry` |
| `mean_luminance` | Histogram mean, 0–1 | below/above thresholds → `too_dark` / `overexposed` |
| `clipped_*_ratio` | Fraction of clipped pixels | above threshold → `overexposed` |
| `horizon_tilt_degrees` | Dominant-line angle estimate | above threshold → `tilted_horizon` |
| `width`/`height` | Decoded dimensions | below rule-profile minimum → `below_min_resolution` |

Thresholds are API-side configuration, not sidecar constants, so tuning needs no redeploy of this
service. The sidecar returns raw measurements **and** its flag interpretation; the API treats the
measurements as authoritative.

**Errors**: `415` unsupported/undecodable image; `422` missing dimension parameters.

## `POST /embed`

Image embedding for near-duplicate detection and zero-shot room pre-classification.

**Request** (`multipart/form-data`): `file` (image).

**Response** `200`:

```json
{
  "model_id": "google/siglip2-base-patch16-224",
  "dimension": 768,
  "embedding": [0.0123, -0.0456, "…"]
}
```

**⚠ UNVERIFIED**: `dimension` must match `photo_embeddings.embedding vector(N)` in
[data-model.md](../data-model.md). The 768 value above is the expected SigLIP2-base dimension but
must be confirmed against the actual model before the migration is written — a mismatch fails at
insert time. The API MUST reject an embedding whose `dimension` differs from the column width
rather than truncating or padding.

**Model identity** is supplied by the API from the `ai_model_registry` row for `photo.embedding`
and echoed back for audit (FR-049). The sidecar MUST NOT hard-code a model id as its own
authority — it reports what it loaded, and the API records it.

**Errors**: `415` unsupported image; `503` model weights not loaded.

## `GET /health`

Readiness for Compose/orchestration. `200` with `{"status":"ok","model_loaded":true}` once
weights are resident; `503` while loading.

## Near-duplicate detection (API-side)

The sidecar only produces vectors. The API computes cosine similarity via pgvector and raises
`near_duplicate` above a **configurable threshold**, surfaced as a flag for the agent to resolve
— never an automatic deletion (spec Edge Cases, FR-011). Exact duplicates are caught earlier by
the `(listing_id, checksum_sha256)` unique constraint at upload.

## Failure behaviour

A sidecar outage degrades to: technical-quality flags unavailable, near-duplicate detection
unavailable, VLM analysis still possible (it does not depend on the sidecar). The listing remains
completable by hand (SC-010). Pipeline steps record `failed` with a reason and are individually
retryable; a sidecar failure never fails the whole pipeline.
