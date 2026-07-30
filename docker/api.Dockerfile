# Highlighter API + pipeline worker.
# Build context is the repo root: docker build -f docker/api.Dockerfile .
#
# The API spawns the pipeline worker (highlighter.dll) as a subprocess, so this
# image carries both binaries plus the media tooling the worker shells out to
# (ffmpeg, yt-dlp, streamlink) and the Python sidecar sources for scene-cut
# detection. TransNetV2/torch is NOT installed by default (multi-GB); shots are
# skipped gracefully without it. Opt in with --build-arg INSTALL_SHOTS=true.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY pipeline-dotnet/src/Highlighter.Pipeline/Highlighter.Pipeline.csproj pipeline-dotnet/src/Highlighter.Pipeline/
COPY pipeline-dotnet/src/Highlighter.Cli/Highlighter.Cli.csproj pipeline-dotnet/src/Highlighter.Cli/
COPY apps/api/src/Highlighter.Api/Highlighter.Api.csproj apps/api/src/Highlighter.Api/
RUN dotnet restore apps/api/src/Highlighter.Api \
    && dotnet restore pipeline-dotnet/src/Highlighter.Cli
COPY pipeline-dotnet/src/ pipeline-dotnet/src/
COPY apps/api/src/ apps/api/src/
RUN dotnet publish apps/api/src/Highlighter.Api -c Release -o /out/api \
    && dotnet publish pipeline-dotnet/src/Highlighter.Cli -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG INSTALL_SHOTS=false

# ffmpeg/ffprobe: capture, render, editor exports. python3 + pip: yt-dlp,
# streamlink (source capture) and the shots sidecar. fonts-dejavu: the API's
# caption rasterizer picks a system font and slim images ship none.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg python3 python3-pip ca-certificates fonts-dejavu-core fonts-liberation \
    && pip3 install --no-cache-dir --break-system-packages yt-dlp streamlink \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out/api ./api
COPY --from=build /out/worker ./worker
# Shots sidecar sources; the worker launches `python3 -m highlighter_pipeline.shots_sidecar`.
COPY pipeline/ ./pipeline/
RUN if [ "$INSTALL_SHOTS" = "true" ]; then \
        pip3 install --no-cache-dir --break-system-packages ./pipeline; \
    fi

# The worker runs with /app as its working directory and writes outputs/ under
# it; mount a volume here if renders must survive restarts.
RUN mkdir -p /app/outputs && chown -R $APP_UID:$APP_UID /app/outputs
ENV Pipeline__RepoRoot=/app \
    Pipeline__WorkerCommand="dotnet /app/worker/highlighter.dll" \
    PYTHONPATH=/app/pipeline \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/api/Highlighter.Api.dll"]
