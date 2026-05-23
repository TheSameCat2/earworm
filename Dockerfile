# syntax=docker/dockerfile:1.7

# ------------------------------------------------------------
# Build stage
# ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-bookworm-slim AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim AS runtime

# Only curl + ca-certs needed at runtime. Audio decoding and source
# resolution (yt-dlp, ffmpeg) live entirely in the sibling Lavalink
# container; this image is pure .NET.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        curl \
        ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Non-root user matching typical Unraid user UID/GID
RUN useradd --system --create-home --uid 1000 earworm

WORKDIR /app
COPY --from=build /app/publish .
COPY conf/earworm.example.yaml /app/conf/earworm.example.yaml

EXPOSE 8080
VOLUME ["/data"]

USER earworm

HEALTHCHECK --interval=30s --timeout=5s --retries=3 --start-period=15s \
    CMD curl -fsSL http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Earworm.dll"]
