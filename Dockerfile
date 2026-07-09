# DropHarvester headless daemon. Builds ONLY the MAUI-free engine (DropHarvester.Core) + the worker
# (DropHarvester.Daemon) - never the desktop MAUI app (which can't build on Linux). The published
# output is portable framework-dependent IL, so a single Dockerfile builds for linux/amd64 AND
# linux/arm64 via `docker buildx --platform` (each stage's base image is arch-matched automatically).
#
# Build:  docker build -t dropharvester-daemon .
# Run:    docker run -d --name dropharvester -v dropharvester-data:/data -p 8080:8080 dropharvester-daemon
# First-run login: `docker logs -f dropharvester` shows a twitch.tv/activate URL + code.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (only the csprojs) so the layer caches until a dependency actually changes.
COPY DropHarvester.Core/DropHarvester.Core.csproj DropHarvester.Core/
COPY DropHarvester.Daemon/DropHarvester.Daemon.csproj DropHarvester.Daemon/
RUN dotnet restore DropHarvester.Daemon/DropHarvester.Daemon.csproj

# Then the sources, and publish (framework-dependent, portable across arch).
COPY DropHarvester.Core/ DropHarvester.Core/
COPY DropHarvester.Daemon/ DropHarvester.Daemon/
RUN dotnet publish DropHarvester.Daemon/DropHarvester.Daemon.csproj -c Release --no-restore -o /app

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# curl is only needed for the container HEALTHCHECK; install it, then run as a non-root user.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home harvester \
    && mkdir -p /data \
    && chown -R harvester:harvester /data

COPY --from=build /app ./

# All persistence (token, settings, stats) lives on the mounted volume.
ENV DROPHARVESTER_DATA=/data \
    DH_HEALTH_PORT=8080
VOLUME /data
EXPOSE 8080

USER harvester

# Liveness for `docker`/compose. Honors DH_HEALTH_PORT if you change it.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS "http://localhost:${DH_HEALTH_PORT:-8080}/healthz" || exit 1

ENTRYPOINT ["dotnet", "DropHarvester.Daemon.dll"]
