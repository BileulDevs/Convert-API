# Build context: repository root (needs global.json and Directory.*.props).
# docker build -f src/ConvertApi.Worker/Dockerfile -t convertapi-worker .
# The conversion engines (FFmpeg, LibreOffice, Pandoc) will be added to the final stage.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so the layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ConvertApi.ServiceDefaults/ConvertApi.ServiceDefaults.csproj src/ConvertApi.ServiceDefaults/
COPY src/ConvertApi.Worker/ConvertApi.Worker.csproj src/ConvertApi.Worker/
RUN dotnet restore src/ConvertApi.Worker/ConvertApi.Worker.csproj

COPY .editorconfig ./
COPY src/ConvertApi.ServiceDefaults/ src/ConvertApi.ServiceDefaults/
COPY src/ConvertApi.Worker/ src/ConvertApi.Worker/
RUN dotnet publish src/ConvertApi.Worker/ConvertApi.Worker.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

# ServiceDefaults references ASP.NET Core (health checks), so the worker needs the aspnet runtime too.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "ConvertApi.Worker.dll"]
