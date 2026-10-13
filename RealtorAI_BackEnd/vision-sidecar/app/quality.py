"""Deterministic technical-quality metrics for listing photos.

No model is involved here, so this path is NOT subject to the Principle VIII.4 model gates:
every number below is a reproducible measurement, which is also why it is cheaper and more
explainable than asking a vision model "is this blurry?".

Contract: specs/002-ai-listing-launch/contracts/vision-sidecar.md

Thresholds are deliberately NOT defined in this module. The API owns them as configuration so
they can be tuned without redeploying the sidecar; this module returns raw measurements plus
its own flag interpretation, and the API treats the measurements as authoritative.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

import cv2
import numpy as np

# Defaults used only for this service's own `flags` convenience field. The API re-evaluates
# against its configured values.
DEFAULT_BLUR_THRESHOLD = 100.0
DEFAULT_DARK_LUMINANCE = 0.25
DEFAULT_BRIGHT_LUMINANCE = 0.85
DEFAULT_CLIPPED_RATIO = 0.05
DEFAULT_TILT_DEGREES = 3.0


@dataclass
class QualityResult:
    width: int
    height: int
    blur_score: float
    mean_luminance: float
    clipped_highlight_ratio: float
    clipped_shadow_ratio: float
    horizon_tilt_degrees: float | None
    flags: list[str] = field(default_factory=list)


def _blur_score(gray: np.ndarray) -> float:
    """Variance of the Laplacian: low variance means few sharp edges, i.e. blur."""
    return float(cv2.Laplacian(gray, cv2.CV_64F).var())


def _luminance_stats(gray: np.ndarray) -> tuple[float, float, float]:
    """Mean luminance plus the fraction of pixels clipped at each end, all normalised 0-1."""
    normalised = gray.astype(np.float32) / 255.0
    total = normalised.size
    clipped_high = float(np.count_nonzero(normalised >= 0.99)) / total
    clipped_low = float(np.count_nonzero(normalised <= 0.01)) / total
    return float(normalised.mean()), clipped_high, clipped_low


def _horizon_tilt_degrees(gray: np.ndarray) -> float | None:
    """Estimate rotation from the dominant near-horizontal line.

    Returns None when no usable line is found — an interior close-up legitimately has no
    horizon, and reporting 0.0 there would falsely assert "level" rather than "unknown".
    """
    edges = cv2.Canny(gray, 50, 150, apertureSize=3)
    lines = cv2.HoughLines(edges, 1, np.pi / 180, threshold=200)
    if lines is None:
        return None

    deviations: list[float] = []
    for line in lines[:50]:
        theta = float(line[0][1])
        # theta near pi/2 is a horizontal line; measure how far off level it is.
        deviation = math.degrees(theta - (math.pi / 2))
        # Keep only near-horizontal candidates; vertical edges (door frames, walls) are not
        # evidence about the horizon.
        if abs(deviation) <= 20.0:
            deviations.append(deviation)

    if not deviations:
        return None

    return round(float(np.median(deviations)), 2)


def analyse(image_bytes: bytes, min_width: int, min_height: int) -> QualityResult:
    """Measure one image. Raises ValueError when the bytes cannot be decoded."""
    buffer = np.frombuffer(image_bytes, dtype=np.uint8)
    image = cv2.imdecode(buffer, cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError("image could not be decoded")

    height, width = image.shape[:2]
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)

    blur = _blur_score(gray)
    mean_luminance, clipped_high, clipped_low = _luminance_stats(gray)
    tilt = _horizon_tilt_degrees(gray)

    flags: list[str] = []
    if blur < DEFAULT_BLUR_THRESHOLD:
        flags.append("blurry")
    if mean_luminance < DEFAULT_DARK_LUMINANCE:
        flags.append("too_dark")
    if mean_luminance > DEFAULT_BRIGHT_LUMINANCE or clipped_high > DEFAULT_CLIPPED_RATIO:
        flags.append("overexposed")
    if tilt is not None and abs(tilt) > DEFAULT_TILT_DEGREES:
        flags.append("tilted_horizon")
    if width < min_width or height < min_height:
        flags.append("below_min_resolution")

    return QualityResult(
        width=width,
        height=height,
        blur_score=round(blur, 2),
        mean_luminance=round(mean_luminance, 4),
        clipped_highlight_ratio=round(clipped_high, 4),
        clipped_shadow_ratio=round(clipped_low, 4),
        horizon_tilt_degrees=tilt,
        flags=flags,
    )
