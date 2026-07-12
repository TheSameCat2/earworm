# syntax=docker/dockerfile:1.7

# ------------------------------------------------------------
# Build stage
# ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Copy csproj first for layer-cache friendliness
COPY src/Earworm/Earworm.csproj Earworm/
RUN dotnet restore Earworm/Earworm.csproj

COPY src/ .
WORKDIR /src/Earworm
RUN dotnet publish -c Release \
        --no-restore \
        -o /app/publish \
        /p:UseAppHost=false

# ------------------------------------------------------------
# Runtime stage
# ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

# Only curl + ca-certs needed at runtime. Audio decoding and source
# resolution (yt-dlp, ffmpeg) live entirely in the sibling Lavalink
# container; this image is pure .NET.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        curl \
        ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Non-root user matching typical Unraid user UID/GID.
# Ubuntu Noble base ships with a placeholder `ubuntu` user at UID 1000, so we
# remove it first before creating our own at the same UID. The `|| true` keeps
# the build going if Microsoft ever drops that default in a future image.
RUN (userdel -r ubuntu 2>/dev/null || true) \
 && useradd --system --create-home --user-group --uid 1000 earworm \
 && mkdir -p /data \
 && chown earworm:earworm /data

WORKDIR /app
COPY --from=build /app/publish .
COPY conf/earworm.example.yaml /app/conf/earworm.example.yaml

EXPOSE 8080
VOLUME ["/data"]

USER earworm

HEALTHCHECK --interval=30s --timeout=5s --retries=3 --start-period=15s \
    CMD curl -fsSL http://localhost:8080/live || exit 1

ENTRYPOINT ["dotnet", "Earworm.dll"]
