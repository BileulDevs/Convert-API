# Build context: repository root (needs global.json and Directory.*.props).
# docker build -f src/ConvertApi.Worker/Dockerfile -t convertapi-worker .
# The document engines (LibreOffice, Pandoc) will be added to the final stage.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so the layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ConvertApi.Core/ConvertApi.Core.csproj src/ConvertApi.Core/
COPY src/ConvertApi.Data/ConvertApi.Data.csproj src/ConvertApi.Data/
COPY src/ConvertApi.ServiceDefaults/ConvertApi.ServiceDefaults.csproj src/ConvertApi.ServiceDefaults/
COPY src/ConvertApi.Worker/ConvertApi.Worker.csproj src/ConvertApi.Worker/
RUN dotnet restore src/ConvertApi.Worker/ConvertApi.Worker.csproj

COPY .editorconfig ./
COPY src/ConvertApi.Core/ src/ConvertApi.Core/
COPY src/ConvertApi.Data/ src/ConvertApi.Data/
COPY src/ConvertApi.ServiceDefaults/ src/ConvertApi.ServiceDefaults/
COPY src/ConvertApi.Worker/ src/ConvertApi.Worker/
RUN dotnet publish src/ConvertApi.Worker/ConvertApi.Worker.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

# ServiceDefaults references ASP.NET Core (health checks), so the worker needs the aspnet runtime too.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# FFmpeg converts audio and video. It runs as a separate process, so its GPL build does not
# extend to this code. ImageMagick ships inside the Magick.NET package.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
# Shared file storage, owned by the non-root user so a volume mounted here is writable.
RUN mkdir /files && chown $APP_UID /files
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "ConvertApi.Worker.dll"]
