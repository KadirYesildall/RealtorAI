"""HTTP contract tests for the vision sidecar — T088.

Contract: specs/002-ai-listing-launch/contracts/vision-sidecar.md

These exercise the service through its HTTP surface, the way the .NET API calls it: field names,
status codes and error mapping. The measurement maths is covered by test_quality.py; here it only
has to come back in the documented shape.

/embed's success path cannot run here for real: the photo.embedding model is disabled and
unverified (Principle VIII.4) and its ML stack is deliberately not installed. So the 503 path is
tested against the real loader — which is what every deployment answers today — and the 200
shape against a substituted embedding function, to pin the response contract the API relies on.
"""

from __future__ import annotations

import cv2
import numpy as np
import pytest
from fastapi.testclient import TestClient

from app import embed as embedding
from app import main

client = TestClient(main.app)


def _png(image: np.ndarray) -> bytes:
    ok, buffer = cv2.imencode(".png", image)
    assert ok
    return buffer.tobytes()


def _checkerboard(size: int = 400, cell: int = 20) -> bytes:
    base = np.indices((size, size)).sum(axis=0) // cell
    grey = ((base % 2) * 255).astype(np.uint8)
    return _png(cv2.cvtColor(grey, cv2.COLOR_GRAY2BGR))


# ─────────────────────────────── /health ───────────────────────────────


def test_health_reports_ok_and_whether_the_model_is_loaded() -> None:
    response = client.get("/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert isinstance(body["model_loaded"], bool)
    assert "model_load_error" in body


# ─────────────────────────────── /quality ───────────────────────────────


def test_quality_returns_every_documented_measurement() -> None:
    response = client.post(
        "/quality",
        files={"file": ("photo.png", _checkerboard(), "image/png")},
        data={"min_width": "100", "min_height": "100"},
    )

    assert response.status_code == 200
    body = response.json()
    assert set(body) == {
        "width",
        "height",
        "blur_score",
        "mean_luminance",
        "clipped_highlight_ratio",
        "clipped_shadow_ratio",
        "horizon_tilt_degrees",
        "flags",
    }
    assert (body["width"], body["height"]) == (400, 400)
    assert isinstance(body["blur_score"], float)
    assert 0.0 <= body["mean_luminance"] <= 1.0
    assert isinstance(body["flags"], list)


def test_quality_applies_the_minimum_dimensions_it_is_given() -> None:
    response = client.post(
        "/quality",
        files={"file": ("photo.png", _checkerboard(size=200, cell=10), "image/png")},
        data={"min_width": "1024", "min_height": "768"},
    )

    assert response.status_code == 200
    assert "below_min_resolution" in response.json()["flags"]


def test_quality_with_undecodable_bytes_is_415() -> None:
    response = client.post(
        "/quality",
        files={"file": ("photo.png", b"not an image", "image/png")},
        data={"min_width": "1", "min_height": "1"},
    )

    assert response.status_code == 415


def test_quality_without_the_minimum_dimensions_is_422() -> None:
    response = client.post("/quality", files={"file": ("photo.png", _checkerboard(), "image/png")})

    assert response.status_code == 422


def test_quality_with_an_empty_file_is_422() -> None:
    response = client.post(
        "/quality",
        files={"file": ("photo.png", b"", "image/png")},
        data={"min_width": "1", "min_height": "1"},
    )

    assert response.status_code == 422


# ─────────────────────────────── /embed ───────────────────────────────


def test_embed_without_loadable_weights_is_503_with_the_reason(monkeypatch: pytest.MonkeyPatch) -> None:
    def unavailable(image_bytes: bytes, model_id: str) -> tuple[list[float], int, str]:
        raise embedding.ModelUnavailableError("No module named 'torch'")

    monkeypatch.setattr(embedding, "embed", unavailable)

    response = client.post(
        "/embed",
        files={"file": ("photo.png", _checkerboard(), "image/png")},
        data={"model_id": "registry/model"},
    )

    assert response.status_code == 503
    assert "torch" in response.json()["detail"]


def test_embed_returns_the_vector_its_dimension_and_the_model_actually_loaded(monkeypatch: pytest.MonkeyPatch) -> None:
    received: dict[str, str] = {}

    def loaded(image_bytes: bytes, model_id: str) -> tuple[list[float], int, str]:
        received["model_id"] = model_id
        return [0.1] * 768, 768, "registry/model@loaded"

    monkeypatch.setattr(embedding, "embed", loaded)

    response = client.post(
        "/embed",
        files={"file": ("photo.png", _checkerboard(), "image/png")},
        data={"model_id": "registry/model"},
    )

    assert response.status_code == 200
    body = response.json()
    # The sidecar uses the model the API names, and reports what it loaded.
    assert received["model_id"] == "registry/model"
    assert body["model_id"] == "registry/model@loaded"
    assert body["dimension"] == 768
    assert len(body["embedding"]) == 768


def test_embed_without_a_model_id_is_422() -> None:
    response = client.post("/embed", files={"file": ("photo.png", _checkerboard(), "image/png")})

    assert response.status_code == 422


def test_embed_against_the_real_loader_in_this_environment_is_503() -> None:
    """The ML stack is not installed (VIII.4), so the real path answers 503 rather than guessing."""
    if embedding.is_loaded():
        pytest.skip("An embedding model is loaded in this environment.")

    response = client.post(
        "/embed",
        files={"file": ("photo.png", _checkerboard(), "image/png")},
        data={"model_id": "google/siglip2-base-patch16-224"},
    )

    assert response.status_code == 503
