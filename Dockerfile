# syntax=docker/dockerfile:1

# Reader source and Go match the reviewed companion revision and its mise pin.
# Build on the builder architecture; only the static target binary enters runtime.
FROM --platform=$BUILDPLATFORM golang:1.27.1-bookworm@sha256:648f440f42a0958804efb24df176f806f9d353b41f1c0627f666428e40310f6b AS catalog-reader-build
ARG TARGETARCH
ENV GOTOOLCHAIN=local
ENV CGO_ENABLED=0
WORKDIR /reader
ADD --checksum=sha256:8ba51d88b827779eeabbe88c56cf10929a4779527429482f1e4227a175d378fe https://codeload.github.com/MoonlarkStudios/romd-dat-catalogs/tar.gz/88ffe6af951f6d09f65a38ab457284b7b657a20e /tmp/reader.tar.gz
RUN tar -xzf /tmp/reader.tar.gz --strip-components=1 \
    && test "$(go env GOVERSION)" = go1.27.1 \
    && GOOS=linux GOARCH="$TARGETARCH" go build -trimpath -o /out/catalog-reader ./cmd/distribution \
    && echo '6ab3222d28db2d8750c85fab1bfd6583b0fd9649252e24764865057200df7459  trust/1.root.json' | sha256sum -c - \
    && cp trust/1.root.json /out/1.root.json

FROM node:24-bookworm-slim@sha256:ba849c60be29959425b8734d57b8b4b7d56f98edd9504c9af091d5281095a71e AS frontend-deps
ENV PNPM_HOME=/pnpm
ENV PATH="${PNPM_HOME}:${PATH}"
WORKDIR /web
RUN corepack enable

COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
COPY web/packages/romd-foundation/package.json packages/romd-foundation/
COPY web/packages/romd-consumer-ui/package.json packages/romd-consumer-ui/
COPY web/packages/romd-admin-api-client/package.json packages/romd-admin-api-client/
COPY web/packages/romd-admin-app/package.json packages/romd-admin-app/
COPY web/packages/romd-consumer-api-client/package.json packages/romd-consumer-api-client/
COPY web/packages/romd-consumer-app/package.json packages/romd-consumer-app/
COPY web/packages/romd-player-app/package.json packages/romd-player-app/
COPY web/packages/romd-player-protocol/package.json packages/romd-player-protocol/
RUN pnpm install --frozen-lockfile

COPY web/ ./

FROM frontend-deps AS frontend-build
RUN pnpm build

FROM frontend-deps AS player-build
RUN pnpm --filter @romd/player-protocol build \
    && pnpm --filter romd-player-app build
COPY deploy/player/write-defaults.mjs /tmp/write-player-defaults.mjs
RUN node /tmp/write-player-defaults.mjs \
    /web/tools/emulatorjs/pin.json \
    /tmp/player-defaults.env

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src

COPY . ./
RUN dotnet restore Romd.sln
RUN dotnet publish src/Romd.Admin.Host/Romd.Admin.Host.csproj \
    --configuration Release \
    --output /out/admin \
    --no-restore \
    -p:BuildFrontend=false \
    -p:SkipMissingFrontendWarning=true
RUN dotnet publish src/Romd.Consumer.Host/Romd.Consumer.Host.csproj \
    --configuration Release \
    --output /out/consumer \
    --no-restore \
    -p:BuildFrontend=false \
    -p:SkipMissingFrontendWarning=true
RUN dotnet publish src/Romd.Worker.Host/Romd.Worker.Host.csproj \
    --configuration Release \
    --output /out/worker \
    --no-restore

# Keep the canonical attribution and original license texts with every image.
FROM scratch AS notices
WORKDIR /notices
COPY LICENSE THIRD_PARTY_NOTICES.md TRADEMARKS.md ./
COPY brand/Funnel-OFL.txt ./brand/
COPY web/packages/romd-foundation/src/fonts/*-OFL.txt ./web/packages/romd-foundation/src/fonts/
COPY reference-data/assets/presentation/platforms/ATTRIBUTION.md ./reference-data/assets/presentation/platforms/
COPY reference-data/assets/presentation/ratings/ATTRIBUTION.md ./reference-data/assets/presentation/ratings/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY --from=notices /notices/ /usr/share/doc/romd/
WORKDIR /app
ENV DOTNET_ENVIRONMENT=Production
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Romd__DataDirectory=/var/lib/romd
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /app /var/lib/romd && chown -R "${APP_UID}:0" /app /var/lib/romd
USER $APP_UID
VOLUME ["/var/lib/romd"]

FROM runtime AS catalog-runtime
ENV DatSubscriptions__Enabled=true
ENV DatSubscriptions__ReaderPath=/app/catalog-reader
ENV DatSubscriptions__RootPath=/app/catalog-trust/1.root.json
ENV DatSubscriptions__Site=https://moonlarkstudios.github.io/romd-dat-data
COPY --from=catalog-reader-build --chmod=755 /out/catalog-reader /app/catalog-reader
COPY --from=catalog-reader-build /out/1.root.json /app/catalog-trust/1.root.json

FROM catalog-runtime AS romd-admin
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=backend-build /out/admin/ ./
COPY --from=frontend-build /web/packages/romd-admin-app/dist/ ./wwwroot/
ENTRYPOINT ["dotnet", "romd-admin.dll"]

FROM runtime AS romd-consumer
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=backend-build /out/consumer/ ./
COPY --from=frontend-build /web/packages/romd-consumer-app/dist/ ./wwwroot/
ENTRYPOINT ["dotnet", "romd-consumer.dll"]

FROM catalog-runtime AS romd-worker
COPY --from=backend-build /out/worker/ ./
ENTRYPOINT ["dotnet", "romd-worker.dll"]

FROM nginx:1.27-alpine AS romd-player
COPY --from=notices /notices/ /usr/share/doc/romd/
EXPOSE 8080
COPY --from=player-build /web/packages/romd-player-app/dist/ /usr/share/nginx/html/
COPY --from=player-build /tmp/player-defaults.env /etc/romd/player-defaults.env
COPY --chmod=755 deploy/player/entrypoint.sh /docker-entrypoint.d/10-romd-player-config.sh
COPY deploy/player/default.conf.template /etc/romd/default.conf.template

FROM catalog-runtime AS romd-admin-dev
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=backend-build /out/admin/ ./
RUN mkdir -p wwwroot && printf '<!doctype html><html></html>' > wwwroot/index.html
ENTRYPOINT ["dotnet", "romd-admin.dll"]

FROM runtime AS romd-consumer-dev
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=backend-build /out/consumer/ ./
RUN mkdir -p wwwroot && printf '<!doctype html><html></html>' > wwwroot/index.html
ENTRYPOINT ["dotnet", "romd-consumer.dll"]
