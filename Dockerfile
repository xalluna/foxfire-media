# syntax=docker/dockerfile:1

# ---- web: admin UI + embed player ------------------------------------------------------------
FROM node:24-alpine AS web
WORKDIR /repo/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
# Vite writes to ../src/Foxfire.Server/wwwroot
RUN npm run build

# ---- build: ASP.NET Core server --------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Foxfire.Server/Foxfire.Server.csproj src/Foxfire.Server/
RUN dotnet restore src/Foxfire.Server/Foxfire.Server.csproj -a $TARGETARCH
COPY src/ src/
COPY --from=web /repo/src/Foxfire.Server/wwwroot src/Foxfire.Server/wwwroot/
RUN dotnet publish src/Foxfire.Server/Foxfire.Server.csproj -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

# ---- runtime ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Optional Ubuntu mirror (e.g. http://azure.archive.ubuntu.com) for networks that cannot reach
# archive.ubuntu.com.
ARG APT_MIRROR=
RUN if [ -n "$APT_MIRROR" ]; then \
      sed -i -e "s|http://archive.ubuntu.com|$APT_MIRROR|g" -e "s|http://security.ubuntu.com|$APT_MIRROR|g" /etc/apt/sources.list.d/ubuntu.sources; \
    fi \
 && apt-get update -o Acquire::Retries=5 \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends -o Acquire::Retries=5 ffmpeg \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data/media /app/config \
 && chown -R "$APP_UID" /data/media

WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Storage__Root=/data/media \
    DOTNET_gcServer=0

# Non-root (the image's built-in "app" user). API keys and secrets are supplied at runtime only.
USER $APP_UID
VOLUME ["/data/media"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "Foxfire.Server.dll"]
