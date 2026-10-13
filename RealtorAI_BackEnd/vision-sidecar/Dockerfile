# Vision sidecar image.
#
# VIII.6: this image contains no image- or video-generation dependency. It measures and embeds
# only. opencv-python-headless is used rather than full opencv so no GUI/codec surface is
# pulled in.

FROM python:3.13-slim

# opencv-python-headless still needs libgl/libglib at runtime for some codecs.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libglib2.0-0 libgomp1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /srv

# Core runtime first so dependency layers cache independently of application code.
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

# The ML stack is intentionally a separate, optional layer: the photo.embedding registry
# entry is still disabled and unverified (Principle VIII.4), so torch is not a hard
# requirement yet. Build with --build-arg WITH_ML=1 once the model is cleared.
ARG WITH_ML=0
COPY requirements-ml.in ./
RUN if [ "$WITH_ML" = "1" ]; then pip install --no-cache-dir -r requirements-ml.in; fi

COPY app ./app

# Run as a non-root user: this service accepts uploaded image bytes from the API, so it is the
# component most exposed to malformed input.
RUN useradd --create-home --uid 10001 sidecar && chown -R sidecar:sidecar /srv
USER sidecar

EXPOSE 8099

CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8099"]
