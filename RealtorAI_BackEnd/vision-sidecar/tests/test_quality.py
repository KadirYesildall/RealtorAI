"""Tests for the deterministic quality metrics — T088 (sidecar contract tests).

These build their own fixtures from numpy arrays with KNOWN properties, which is the whole
point: a sharp checkerboard genuinely has high Laplacian variance, a Gaussian-blurred copy
genuinely has low variance, and a near-black frame genuinely has low mean luminance. That
makes each assertion a real measurement rather than a tautology.

Contrast this with the 12-image listing photo set, which is NOT synthesised (see
RealtorAI_BackEnd/supabase/seed.sql): a generated placeholder cannot be meaningfully "a photo
containing a face", so fabricating one there would defeat the detector it is meant to test.
Synthetic fixtures are used here precisely because blur, exposure, and resolution ARE
synthesisable properties.
"""

from __future__ import annotations

import cv2
import numpy as np
import pytest

from app import quality


def _encode(image: np.ndarray) -> bytes:
    ok, buffer = cv2.imencode(".png", image)
    assert ok, "fixture failed to encode"
    return buffer.tobytes()


def _sharp_checkerboard(size: int = 600, cell: int = 20) -> np.ndarray:
    """High-frequency edges in every direction -> high Laplacian variance."""
    base = np.indices((size, size)).sum(axis=0) // cell
    grey = ((base % 2) * 255).astype(np.uint8)
    return cv2.cvtColor(grey, cv2.COLOR_GRAY2BGR)


def test_sharp_image_has_higher_blur_score_than_blurred_copy() -> None:
    sharp = _sharp_checkerboard()
    blurred = cv2.GaussianBlur(sharp, (31, 31), 0)

    sharp_result = quality.analyse(_encode(sharp), min_width=1, min_height=1)
    blurred_result = quality.analyse(_encode(blurred), min_width=1, min_height=1)

    assert sharp_result.blur_score > blurred_result.blur_score
    assert "blurry" in blurred_result.flags
    assert "blurry" not in sharp_result.flags


def test_dark_image_is_flagged_too_dark() -> None:
    dark = np.full((400, 400, 3), 10, dtype=np.uint8)

    result = quality.analyse(_encode(dark), min_width=1, min_height=1)

    assert result.mean_luminance < quality.DEFAULT_DARK_LUMINANCE
    assert "too_dark" in result.flags


def test_blown_out_image_is_flagged_overexposed() -> None:
    bright = np.full((400, 400, 3), 253, dtype=np.uint8)

    result = quality.analyse(_encode(bright), min_width=1, min_height=1)

    assert result.clipped_highlight_ratio > quality.DEFAULT_CLIPPED_RATIO
    assert "overexposed" in result.flags


def test_small_image_is_flagged_below_min_resolution() -> None:
    small = _sharp_checkerboard(size=200, cell=10)

    result = quality.analyse(_encode(small), min_width=1024, min_height=768)

    assert result.width == 200
    assert "below_min_resolution" in result.flags


def test_dimensions_are_reported_accurately() -> None:
    image = np.zeros((480, 640, 3), dtype=np.uint8)

    result = quality.analyse(_encode(image), min_width=1, min_height=1)

    assert (result.width, result.height) == (640, 480)


def test_interior_closeup_without_a_horizon_reports_none_not_zero() -> None:
    """A flat frame has no horizon. Reporting 0.0 would falsely assert "level"."""
    flat = np.full((300, 300, 3), 128, dtype=np.uint8)

    result = quality.analyse(_encode(flat), min_width=1, min_height=1)

    assert result.horizon_tilt_degrees is None
    assert "tilted_horizon" not in result.flags


def test_undecodable_bytes_raise_value_error() -> None:
    with pytest.raises(ValueError):
        quality.analyse(b"this is definitely not an image", min_width=1, min_height=1)
