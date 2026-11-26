# Build context: repository root (needs global.json and Directory.*.props).
# docker build -f src/ConvertApi.Api/Dockerfile -t convertapi-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so the layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ConvertApi.Core/ConvertApi.Core.csproj src/ConvertApi.Core/
COPY src/ConvertApi.Data/ConvertApi.Data.csproj src/ConvertApi.Data/
COPY src/ConvertApi.ServiceDefaults/ConvertApi.ServiceDefaults.csproj src/ConvertApi.ServiceDefaults/
COPY src/ConvertApi.Api/ConvertApi.Api.csproj src/ConvertApi.Api/
RUN dotnet restore src/ConvertApi.Api/ConvertApi.Api.csproj

COPY .editorconfig ./
COPY src/ConvertApi.Core/ src/ConvertApi.Core/
COPY src/ConvertApi.Data/ src/ConvertApi.Data/
COPY src/ConvertApi.ServiceDefaults/ src/ConvertApi.ServiceDefaults/
COPY src/ConvertApi.Api/ src/ConvertApi.Api/
RUN dotnet publish src/ConvertApi.Api/ConvertApi.Api.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

# The web front (web/): static files built here, served by the API. Node stays in this stage.
FROM node:26-alpine@sha256:6bd9ab43c0ec0190ca7b2d193906cb8b7edf6e7f4afa43ddeccd6452710bffb5 AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Shared file storage, owned by the non-root user so a volume mounted here is writable.
RUN mkdir /files && chown $APP_UID /files
USER $APP_UID
EXPOSE 8080
COPY --from=build /app .
COPY --from=web /web/.output/public ./wwwroot
ENTRYPOINT ["dotnet", "ConvertApi.Api.dll"]
