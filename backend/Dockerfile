# syntax=docker/dockerfile:1.7

# =============================================================================================
# Build
# =============================================================================================
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build

ARG TARGETARCH
WORKDIR /src

# Manifests first, restore, then sources. Restore is the slow step and its inputs change far
# less often than the code, so ordering it this way means a source-only edit reuses the cached
# package layer instead of re-downloading the graph on every build.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/FinMonitor.Core/FinMonitor.Core.csproj src/FinMonitor.Core/
COPY src/FinMonitor.Redis/FinMonitor.Redis.csproj src/FinMonitor.Redis/
COPY src/FinMonitor.Api/FinMonitor.Api.csproj src/FinMonitor.Api/

RUN dotnet restore src/FinMonitor.Api/FinMonitor.Api.csproj -a $TARGETARCH

COPY src/ src/

RUN dotnet publish src/FinMonitor.Api/FinMonitor.Api.csproj \
    -c Release \
    -a $TARGETARCH \
    --no-restore \
    -o /app \
    # No apphost: the image invokes `dotnet FinMonitor.Api.dll`, so the native launcher stub
    # would be dead weight.
    -p:UseAppHost=false

# =============================================================================================
# Runtime
# =============================================================================================
# The Alpine ASP.NET runtime rather than the Debian-based default: roughly 110 MB against
# ~220 MB, with a correspondingly smaller CVE surface.
#
# Trimming and Native AOT were considered and rejected. Both would cut this significantly, but
# SignalR's hub dispatch and System.Text.Json's reflection-based serialisation are not reliably
# trim-safe without moving the whole contract onto source generators. That is worthwhile work,
# and the wrong thing to have silently in an MVP: the failure mode is a missing member at
# runtime, in production, not a build error.
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime

WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_TieredPMStubs=0

COPY --from=build /app .

# The base image ships a non-root user; $APP_UID is the id it was created with. Running as root
# in a container is the default nobody should accept, and the Kubernetes manifests additionally
# enforce it at the pod level so the image cannot quietly regress.
USER $APP_UID

EXPOSE 8080

# Compose and plain `docker run` use this. Kubernetes ignores it in favour of the probes in
# deployment.yaml, which distinguish liveness from readiness — a distinction this cannot express.
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget --quiet --spider http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "FinMonitor.Api.dll"]
