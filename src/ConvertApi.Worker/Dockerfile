# Build context: repository root (needs global.json and Directory.*.props).
# docker build -f src/ConvertApi.Worker/Dockerfile -t convertapi-worker .
# Needs BuildKit (the default builder), which sets TARGETARCH.
# The document engines (LibreOffice, Pandoc) will be added to the final stage.

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
# Restore and publish for the image's architecture only: Magick.NET otherwise ships its native
# library for every platform (Windows, macOS, ARM, musl), some 200 MB the container never loads.
ARG TARGETARCH
WORKDIR /src

# Restore first, from project files only, so the layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ConvertApi.Core/ConvertApi.Core.csproj src/ConvertApi.Core/
COPY src/ConvertApi.Data/ConvertApi.Data.csproj src/ConvertApi.Data/
COPY src/ConvertApi.ServiceDefaults/ConvertApi.ServiceDefaults.csproj src/ConvertApi.ServiceDefaults/
COPY src/ConvertApi.Worker/ConvertApi.Worker.csproj src/ConvertApi.Worker/
RUN dotnet restore src/ConvertApi.Worker/ConvertApi.Worker.csproj -a $TARGETARCH

COPY .editorconfig ./
COPY src/ConvertApi.Core/ src/ConvertApi.Core/
COPY src/ConvertApi.Data/ src/ConvertApi.Data/
COPY src/ConvertApi.ServiceDefaults/ src/ConvertApi.ServiceDefaults/
COPY src/ConvertApi.Worker/ src/ConvertApi.Worker/
RUN dotnet publish src/ConvertApi.Worker/ConvertApi.Worker.csproj \
    --configuration Release --no-restore -a $TARGETARCH --self-contained false --output /app -p:UseAppHost=false

# A static FFmpeg build (github.com/wader/static-ffmpeg), pinned by digest: one self-contained
# binary instead of Ubuntu's ffmpeg package and its hundreds of MB of shared libraries.
FROM mwader/static-ffmpeg:9.1@sha256:dd09509d8c2152bfa601c8c20e3dd166bde906d37951b9f7e8bbd922267f5ac9 AS ffmpeg

# Just the two binaries, for CI to test with the very build shipped here (never part of the image):
# docker buildx build --target ffmpeg-binaries --output type=local,dest=<dir> -f src/ConvertApi.Worker/Dockerfile .
FROM scratch AS ffmpeg-binaries
COPY --from=ffmpeg /ffmpeg /ffprobe /

# ServiceDefaults references ASP.NET Core (health checks), so the worker needs the aspnet runtime too.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# FFmpeg converts audio and video. It runs as a separate process, so its GPL build does not
# extend to this code (see THIRD-PARTY-NOTICES.md). ImageMagick ships inside the Magick.NET package.
COPY --from=ffmpeg /ffmpeg /usr/local/bin/ffmpeg

WORKDIR /app
# Shared file storage and the conversion scratch space, owned by the non-root user so the
# volumes mounted there are writable (the container's own file system is read-only).
RUN mkdir /files /work && chown $APP_UID /files /work
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "ConvertApi.Worker.dll"]
