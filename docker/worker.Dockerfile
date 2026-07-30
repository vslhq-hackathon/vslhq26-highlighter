# Highlighter pipeline worker for Container Apps Jobs.
# Build context is the repo root: docker build -f docker/worker.Dockerfile .
#
# One image = one queued pipeline run: the entrypoint (`highlighter run-queued`)
# drains the pipeline-jobs storage queue and execs the requested verb as a child
# process. Unlike the API image, TransNetV2 IS installed here (CPU torch — the
# model runs on 48x27 frames, no GPU needed), because this is where scene-cut
# detection actually executes in production.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY pipeline-dotnet/src/Highlighter.Pipeline/Highlighter.Pipeline.csproj pipeline-dotnet/src/Highlighter.Pipeline/
COPY pipeline-dotnet/src/Highlighter.Cli/Highlighter.Cli.csproj pipeline-dotnet/src/Highlighter.Cli/
RUN dotnet restore pipeline-dotnet/src/Highlighter.Cli
COPY pipeline-dotnet/src/ pipeline-dotnet/src/
RUN dotnet publish pipeline-dotnet/src/Highlighter.Cli -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG INSTALL_SHOTS=true

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg python3 python3-pip ca-certificates fonts-dejavu-core fonts-liberation \
    && pip3 install --no-cache-dir --break-system-packages yt-dlp streamlink \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out/worker ./worker
# Shots sidecar; CPU-only torch first so transnetv2-pytorch doesn't pull the
# multi-GB CUDA wheel.
COPY pipeline/ ./pipeline/
RUN if [ "$INSTALL_SHOTS" = "true" ]; then \
        pip3 install --no-cache-dir --break-system-packages \
            torch --index-url https://download.pytorch.org/whl/cpu \
        && pip3 install --no-cache-dir --break-system-packages ./pipeline; \
    fi

# Same layout contract as the API container: cwd /app, renders under
# /app/outputs (an Azure Files mount shared with the API's /media mirror).
RUN mkdir -p /app/outputs && chown -R $APP_UID:$APP_UID /app/outputs
ENV PYTHONPATH=/app/pipeline

USER $APP_UID
ENTRYPOINT ["dotnet", "/app/worker/highlighter.dll", "run-queued"]
