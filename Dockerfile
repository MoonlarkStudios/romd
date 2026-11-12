# syntax=docker/dockerfile:1@sha256:4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e

# Reader source remains at the reviewed companion revision. Service the compiler
# patch independently; keep the build assertion and binary receipt in sync.
# Build on the builder architecture; only the static target binary enters runtime.
FROM --platform=$BUILDPLATFORM golang:1.27.4-bookworm@sha256:4fa7c968ba93f1635670522dbb01a2c78150443e1385746924566079f36570ea AS catalog-reader-build
ARG TARGETARCH
ENV GOTOOLCHAIN=local
ENV CGO_ENABLED=0
WORKDIR /reader
ADD --checksum=sha256:d325486112281b0ef24d46e4ffce2fa28472359baf0829751de87de751f1f6cb https://codeload.github.com/MoonlarkStudios/romd-dat-catalogs/tar.gz/f41d480da850f534044d0ee690747af2ab5cd3c5 /tmp/reader.tar.gz
RUN tar -xzf /tmp/reader.tar.gz --strip-components=1 \
    && test "$(go env GOVERSION)" = go1.27.4 \
    && GOOS=linux GOARCH="$TARGETARCH" go build -trimpath -o /out/catalog-reader ./cmd/distribution \
    && echo '6ab3222d28db2d8750c85fab1bfd6583b0fd9649252e24764865057200df7459  trust/1.root.json' | sha256sum -c - \
    && cp trust/1.root.json /out/1.root.json \
    && printf '{"schemaVersion":1,"module":"github.com/MoonlarkStudios/romd-dat-catalogs","sourceCommit":"f41d480da850f534044d0ee690747af2ab5cd3c5","binarySha256":"%s","goVersion":"go1.27.4"}\n' "$(sha256sum /out/catalog-reader | cut -d ' ' -f 1)" > /out/catalog-reader.json

# Qualification-only provenance and scanner exports; these tools never enter runtime images.
FROM catalog-reader-build AS catalog-reader-security
RUN GOOS=linux GOARCH="$TARGETARCH" go list -deps -json ./cmd/distribution > /out/packages.json \
    && go version -m -json /out/catalog-reader > /out/buildinfo.json \
    && cp go.mod go.sum /tmp/reader.tar.gz /out/

FROM catalog-reader-build AS govulncheck-build
WORKDIR /govulncheck
ENV GOPROXY=https://proxy.golang.org
ENV GOSUMDB=sum.golang.org
ENV GOMAXPROCS=2
ENV GOFLAGS=-p=2
RUN mkdir -p /tools \
    && go mod download -json golang.org/x/vuln@v1.9.0 > /tools/govulncheck-module.json \
    && grep -F '"Sum": "h1:12VaqnI9kJfqwRoPptxoqsUhQtdfrSbdksT7X/KY/sc="' /tools/govulncheck-module.json \
    && grep -F '"GoModSum": "h1:E/Frwm0xp49fTyBW+6CQHdkvobknbwUM49yEpMGUauw="' /tools/govulncheck-module.json \
    && cp /go/pkg/mod/cache/download/golang.org/x/vuln/@v/v1.9.0.zip /tools/govulncheck-source.zip \
    && echo 'e4bcdca4303ab3e57e913d0b086bbf7916b97ed4d0b069c8e0eede39e8782c2c  /tools/govulncheck-source.zip' | sha256sum -c - \
    && GOOS=linux GOARCH=amd64 GOBIN=/tools go install golang.org/x/vuln/cmd/govulncheck@v1.9.0 \
    && go version -m -json /tools/govulncheck > /tools/govulncheck-buildinfo.json

FROM scratch AS catalog-reader-security-artifacts
COPY --from=catalog-reader-security /out/ /
COPY --from=govulncheck-build /tools/ /

# Native builder for libchdr and chdman. AlmaLinux 8.10 keeps the glibc 2.28
# baseline under the GLIBC <= 2.31 native policy; RPMs are the reviewed pins and
# tool archives are checksum-pinned.
FROM almalinux:8.10@sha256:8b469a3a78515e8a18ea8fc727e6a3679e1d0c5ba6f58d5b30be3d0d9b86cffe AS native-builder
ARG TARGETARCH
COPY native/builder/linux-x64.rpm.txt native/builder/linux-arm64.rpm.txt /opt/rpm-pins/
# The pinned toolchain uses only these repositories; unrelated Extras metadata
# must not become a native-build dependency.
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dnf --disablerepo='*' --enablerepo=baseos,appstream,powertools install -y $(cat "/opt/rpm-pins/$rid.rpm.txt") \
    && dnf clean all \
    && echo "$rid" > /opt/native-rid
RUN case "$TARGETARCH" in \
      amd64) sdk=x64; cmake=x86_64; \
        sdk_sha512=1904a98dda2db34ba78ba3c4fb1b224455329af91c341d83547843c1839e112b04ae699ee92fa2449a8f62249196a938723078c0c60b9f65c88539d5687729c2; \
        cmake_sha256=0dc2e9a6860f06bf10bd8fadc03e35d9eeb4df46e33763a7e480e987758f385c ;; \
      arm64) sdk=arm64; cmake=aarch64; \
        sdk_sha512=db85ae85527251fbb1290720ac3bf324bb1d9eea74516ef291ed4bb1f8ff6ec63a17593367f49b456115e8b24ead6d0c2ece2801e501f36a6cd4444ed01a498f; \
        cmake_sha256=83f8fd91d2038a56556e1400390fcfe42f79602940c494f6c6f1cdae7f9e7f40 ;; \
      *) exit 1 ;; \
    esac \
    && curl --fail --location --proto '=https' --proto-redir '=https' --max-time 600 --output /tmp/sdk.tar.gz \
      "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-linux-$sdk.tar.gz" \
    && echo "$sdk_sha512  /tmp/sdk.tar.gz" | sha512sum -c - \
    && curl --fail --location --proto '=https' --proto-redir '=https' --max-time 300 --output /tmp/cmake.tar.gz \
      "https://github.com/Kitware/CMake/releases/download/v3.31.12/cmake-3.31.12-linux-$cmake.tar.gz" \
    && echo "$cmake_sha256  /tmp/cmake.tar.gz" | sha256sum -c - \
    && mkdir -p /opt/dotnet /opt/cmake \
    && tar xzf /tmp/sdk.tar.gz -C /opt/dotnet \
    && tar xzf /tmp/cmake.tar.gz -C /opt/cmake --strip-components=1 \
    && rm /tmp/sdk.tar.gz /tmp/cmake.tar.gz
ENV PATH="/opt/dotnet:/opt/cmake/bin:${PATH}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY Directory.Packages.props ./
COPY build/Moonlark.Native.props build/
COPY tools/Moonlark.Native.Engineering/ tools/Moonlark.Native.Engineering/
# Cold SDK startup needs its shared mutex directory on rootless overlay storage.
RUN install -d -m 1777 /tmp/.dotnet \
    && dotnet build tools/Moonlark.Native.Engineering -c Release -nologo -p:RestoreLockedMode=true \
    && ln -s /src/tools/Moonlark.Native.Engineering/bin/Release/net10.0/Moonlark.Native.Engineering.dll /opt/native-cli.dll

# libchdr from the pinned submodule commit. The build verifies that source's git
# identity, so the stage clones the exact pinned commit instead of copying it.
FROM native-builder AS libchdr-build
COPY native/libchdr/ native/libchdr/
RUN commit="$(sed -n 's/^ *"commit": *"\([0-9a-f]\{40\}\)",$/\1/p' native/libchdr/libchdr.json)" \
    && test "${#commit}" -eq 40 \
    && git clone --quiet https://github.com/rtissera/libchdr.git native/libchdr/upstream \
    && git -C native/libchdr/upstream checkout --quiet --detach "$commit"
RUN rid="$(cat /opt/native-rid)" \
    && dotnet /opt/native-cli.dll native build --rid "$rid" \
    && dotnet /opt/native-cli.dll native verify --manifest "artifacts/native/libchdr/$rid/build-manifest.json" \
    && mkdir -p /out && cp -a "artifacts/native/libchdr/$rid/." /out/

FROM scratch AS libchdr-artifacts
COPY --from=libchdr-build /out/ /

# The builder's Clang 21.1.8 builtin headers, for the ClangSharp drift check on Linux.
FROM native-builder AS clang-resources-build
RUN clang --version | grep -q '^clang version 21\.1\.8 ' \
    && mkdir -p /out && cp -R "$(clang -print-resource-dir)/include" /out/include

FROM scratch AS clang-resources
COPY --from=clang-resources-build /out/ /

# chdman from the pinned MAME tarball; ADD checks its SHA-256, so this stage is
# cached by it. The build re-checks size and digest against native/chdman/mame.json.
FROM native-builder AS chdman-build
ARG CHDMAN_JOBS=2
ADD --checksum=sha256:0929cc749afabcef892900e10dd90bd8b05f94a7dde8f367ac6a5d2082589f84 \
    https://github.com/mamedev/mame/archive/refs/tags/mame0289.tar.gz /inputs/mame0289.tar.gz
ADD --checksum=sha256:5f5993c530f084535c65a6879e9b26ad441169b3e25d789d83287040a9ca5165 \
    https://github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10.tar.gz /inputs/SDL2-2.32.10.tar.gz
RUN mkdir -p /inputs/sdl/include/SDL2 \
    && tar xzf /inputs/SDL2-2.32.10.tar.gz --strip-components=2 -C /inputs/sdl/include/SDL2 \
      --wildcards 'SDL2-2.32.10/include/*.h'
COPY native/libchdr/libchdr.json native/libchdr/
COPY native/chdman/ native/chdman/
RUN rid="$(cat /opt/native-rid)" \
    && dotnet /opt/native-cli.dll chdman build --archive /inputs/mame0289.tar.gz \
      --sdl-include /inputs/sdl/include --jobs "$CHDMAN_JOBS" \
      --output "artifacts/tools/chdman/$rid" --log artifacts/chdman-build.log \
    && mkdir -p /out \
    && cp -a "artifacts/tools/chdman/$rid/build-manifest.json" "artifacts/tools/chdman/$rid/native" \
      "artifacts/tools/chdman/$rid/source-inputs.json" /out/

FROM scratch AS chdman-artifacts
COPY --from=chdman-build /out/ /

FROM node:24.22.0-bookworm-slim@sha256:a5f4366e834695d5f363b920c104953a535d3a9864d1bdf74566a0ebe01daee5 AS frontend-deps
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
COPY web/packages/romd-delivery/package.json packages/romd-delivery/
COPY web/packages/romd-portal-auth/package.json packages/romd-portal-auth/
COPY web/patches/ patches/
RUN pnpm install --frozen-lockfile

COPY web/ ./
COPY scripts/security/frontend_inventory.mjs /tmp/frontend_inventory.mjs

FROM frontend-deps AS frontend-build
RUN pnpm build \
    && mkdir -p /out \
    && pnpm --filter romd-admin-app list --prod --depth Infinity --json > /tmp/admin-deps.json \
    && node /tmp/frontend_inventory.mjs --input /tmp/admin-deps.json --application admin --output /out/admin-frontend.cdx.json \
    && pnpm --filter romd-consumer-app list --prod --depth Infinity --json > /tmp/consumer-deps.json \
    && node /tmp/frontend_inventory.mjs --input /tmp/consumer-deps.json --application consumer --output /out/consumer-frontend.cdx.json

FROM frontend-deps AS player-build
RUN pnpm --filter @romd/player-protocol build \
    && pnpm --filter romd-player-app build \
    && mkdir -p /out \
    && pnpm --filter romd-player-app list --prod --depth Infinity --json > /tmp/player-deps.json \
    && node /tmp/frontend_inventory.mjs --input /tmp/player-deps.json --application player --emulatorjs-pin /web/tools/emulatorjs/pin.json --output /out/player-frontend.cdx.json
COPY deploy/player/write-defaults.mjs /tmp/write-player-defaults.mjs
RUN node /tmp/write-player-defaults.mjs \
    /web/tools/emulatorjs/pin.json \
    /tmp/player-defaults.env

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS backend-build
ARG TARGETARCH
WORKDIR /src

COPY . ./
# Server images do not need the native mount client or test projects from the solution.
RUN case "$TARGETARCH" in arm64) rid=linux-arm64 ;; amd64) rid=linux-x64 ;; *) exit 1 ;; esac \
    && dotnet restore src/Romd.Admin.Host/Romd.Admin.Host.csproj --runtime "$rid" --lock-file-path "packages.$rid.lock.json" --locked-mode \
    && dotnet restore src/Romd.Consumer.Host/Romd.Consumer.Host.csproj --runtime "$rid" --lock-file-path "packages.$rid.lock.json" --locked-mode \
    && dotnet restore src/Romd.Worker.Host/Romd.Worker.Host.csproj --runtime "$rid" --lock-file-path "packages.$rid.lock.json" --locked-mode \
    && dotnet restore tools/Romd.Media.Probe/Romd.Media.Probe.csproj --runtime "$rid" --lock-file-path "packages.$rid.lock.json" --locked-mode
RUN case "$TARGETARCH" in arm64) rid=linux-arm64 ;; amd64) rid=linux-x64 ;; *) exit 1 ;; esac \
    && dotnet publish src/Romd.Admin.Host/Romd.Admin.Host.csproj \
    --configuration Release --runtime "$rid" --self-contained false \
    --output /out/admin \
    --no-restore \
    --disable-build-servers -m:1 -warnaserror -p:UseSharedCompilation=false \
    -p:BuildFrontend=false \
    -p:SkipMissingFrontendWarning=true
RUN case "$TARGETARCH" in arm64) rid=linux-arm64 ;; amd64) rid=linux-x64 ;; *) exit 1 ;; esac \
    && dotnet publish src/Romd.Consumer.Host/Romd.Consumer.Host.csproj \
    --configuration Release --runtime "$rid" --self-contained false \
    --output /out/consumer \
    --no-restore \
    --disable-build-servers -m:1 -warnaserror -p:UseSharedCompilation=false \
    -p:BuildFrontend=false \
    -p:SkipMissingFrontendWarning=true
RUN case "$TARGETARCH" in arm64) rid=linux-arm64 ;; amd64) rid=linux-x64 ;; *) exit 1 ;; esac \
    && dotnet publish src/Romd.Worker.Host/Romd.Worker.Host.csproj \
    --configuration Release --runtime "$rid" --self-contained false \
    --output /out/worker \
    --no-restore \
    --disable-build-servers -m:1 -warnaserror -p:UseSharedCompilation=false
RUN case "$TARGETARCH" in arm64) rid=linux-arm64 ;; amd64) rid=linux-x64 ;; *) exit 1 ;; esac \
    && dotnet publish tools/Romd.Media.Probe/Romd.Media.Probe.csproj \
    --configuration Release --runtime "$rid" --self-contained false --output /out/qualification --no-restore \
    --disable-build-servers -m:1 -warnaserror -p:UseSharedCompilation=false \
    -p:BuildFrontend=false -p:SkipMissingFrontendWarning=true

# Keep the canonical attribution and original license texts with every image.
FROM scratch AS notices
WORKDIR /notices
COPY LICENSE THIRD_PARTY_NOTICES.md TRADEMARKS.md ./
COPY brand/fonts/Fraunces-OFL.txt ./brand/fonts/
COPY web/packages/romd-foundation/src/fonts/*-OFL.txt ./web/packages/romd-foundation/src/fonts/
COPY reference-data/assets/presentation/platforms/ATTRIBUTION.md ./reference-data/assets/presentation/platforms/
COPY reference-data/assets/presentation/ratings/ATTRIBUTION.md ./reference-data/assets/presentation/ratings/

# Notices for the CHD binaries, at their repository paths so THIRD_PARTY_NOTICES.md
# links resolve. libchdr ships in every server image; chdman only in the worker.
FROM scratch AS libchdr-notices
COPY native/libchdr/notices/LICENSE.txt /native/libchdr/notices/
COPY src/Moonlark.Libchdr/LICENSE /src/Moonlark.Libchdr/

# chdman-artifacts keeps only the binary, its record and the source inventory, so the
# license texts come from the build's own extracted, digest-checked MAME source.
FROM chdman-build AS chdman-license-texts
RUN mkdir -p /license \
    && tar xzf /inputs/SDL2-2.32.10.tar.gz --strip-components=1 -C /license SDL2-2.32.10/LICENSE.txt \
    && cd "artifacts/tools/chdman/$(cat /opt/native-rid)/source" \
    && cp --parents -R mame-mame0289/COPYING mame-mame0289/docs/legal \
      mame-mame0289/3rdparty/expat/COPYING mame-mame0289/3rdparty/flac/COPYING.FDL \
      mame-mame0289/3rdparty/flac/COPYING.GPL mame-mame0289/3rdparty/flac/COPYING.LGPL \
      mame-mame0289/3rdparty/flac/COPYING.Xiph mame-mame0289/3rdparty/lzma/DOC/7zC.txt \
      mame-mame0289/3rdparty/utf8proc/LICENSE.md mame-mame0289/3rdparty/zlib/LICENSE \
      mame-mame0289/3rdparty/zstd/COPYING mame-mame0289/3rdparty/zstd/LICENSE /license/

# License texts from the verified MAME archive, the SDL archive whose headers the
# build read, and the builder whose static C/C++ runtime chdman links.
FROM scratch AS chdman-notices
COPY native/chdman/notices/ /native/chdman/notices/
COPY --from=chdman-license-texts /license/mame-mame0289/COPYING /native/chdman/notices/mame-mame0289/
COPY --from=chdman-license-texts /license/mame-mame0289/docs/legal/ /native/chdman/notices/mame-mame0289/docs/legal/
COPY --from=chdman-license-texts /license/mame-mame0289/3rdparty/expat/COPYING /native/chdman/notices/mame-mame0289/3rdparty/expat/
COPY --from=chdman-license-texts /license/mame-mame0289/3rdparty/flac/COPYING.FDL /license/mame-mame0289/3rdparty/flac/COPYING.GPL \
    /license/mame-mame0289/3rdparty/flac/COPYING.LGPL /license/mame-mame0289/3rdparty/flac/COPYING.Xiph \
    /native/chdman/notices/mame-mame0289/3rdparty/flac/
COPY --from=chdman-license-texts /license/mame-mame0289/3rdparty/lzma/DOC/7zC.txt /native/chdman/notices/mame-mame0289/3rdparty/lzma/DOC/
COPY --from=chdman-license-texts /license/mame-mame0289/3rdparty/utf8proc/LICENSE.md /native/chdman/notices/mame-mame0289/3rdparty/utf8proc/
COPY --from=chdman-license-texts /license/mame-mame0289/3rdparty/zlib/LICENSE /native/chdman/notices/mame-mame0289/3rdparty/zlib/
COPY --from=chdman-license-texts /license/mame-mame0289/3rdparty/zstd/COPYING /license/mame-mame0289/3rdparty/zstd/LICENSE \
    /native/chdman/notices/mame-mame0289/3rdparty/zstd/
COPY --from=chdman-license-texts /license/LICENSE.txt /native/chdman/notices/SDL2-LICENSE.txt
COPY --from=native-builder /opt/rh/gcc-toolset-15/root/usr/share/doc/gcc-toolset-15-gcc/COPYING* /native/chdman/notices/runtime/gcc-toolset-15/
COPY --from=native-builder /usr/share/licenses/glibc/ /native/chdman/notices/runtime/glibc-2.28/

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS runtime
ARG ROMD_SOURCE_REVISION=development
ARG ROMD_RELEASE_TAG=development
LABEL org.opencontainers.image.revision=$ROMD_SOURCE_REVISION org.opencontainers.image.version=$ROMD_RELEASE_TAG \
    org.opencontainers.image.source=https://github.com/MoonlarkStudios/romd org.opencontainers.image.licenses=AGPL-3.0-only
COPY --from=notices /notices/ /usr/share/doc/romd/
COPY --from=libchdr-notices / /usr/share/doc/romd/
# Only the build record and the binary, with the build's file modes. (--chmod would
# also apply to the directories COPY creates.) Both Linux runtime stages name this directory.
COPY --from=libchdr-artifacts /build-manifest.json /app/native/libchdr/build-manifest.json
COPY --from=libchdr-artifacts /native/libmoonlark_chdr.so /app/native/libchdr/native/libmoonlark_chdr.so
WORKDIR /app
ENV DOTNET_ENVIRONMENT=Production
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Romd__DataDirectory=/var/lib/romd
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
# Native files must be root-owned and not group- or world-writable.
RUN test -z "$(find /app/native \( ! -user root -o -perm /022 \) -print)" \
    && ldd /app/native/libchdr/native/libmoonlark_chdr.so > /tmp/chd-dependencies \
    && ! grep -q 'not found' /tmp/chd-dependencies \
    && cat /tmp/chd-dependencies \
    && rm /tmp/chd-dependencies \
    && mkdir -p /app /var/lib/romd && chown -R "${APP_UID}:0" /var/lib/romd
USER $APP_UID
VOLUME ["/var/lib/romd"]

# Both Linux architectures carry native CHD assets; recipe-2 qualification is recorded per RID.
# A blank or partial Romd__Native__ override refuses startup; there is no opt-out.
FROM runtime AS runtime-amd64
ENV Romd__Native__LibchdrDirectory=/app/native/libchdr

FROM runtime AS runtime-arm64
ENV Romd__Native__LibchdrDirectory=/app/native/libchdr

FROM runtime-${TARGETARCH} AS catalog-runtime
ENV DatSubscriptions__Enabled=true
ENV DatSubscriptions__ReaderPath=/app/catalog-reader
ENV DatSubscriptions__RootPath=/app/catalog-trust/1.root.json
ENV DatSubscriptions__Site=https://moonlarkstudios.github.io/romd-dat-data
COPY --from=catalog-reader-build --chmod=755 /out/catalog-reader /app/catalog-reader
COPY --from=catalog-reader-build /out/1.root.json /app/catalog-trust/1.root.json
COPY --from=catalog-reader-build /out/catalog-reader.json /usr/share/romd/sbom/catalog-reader.json

FROM catalog-runtime AS romd-admin
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=backend-build /out/admin/ ./
COPY --from=frontend-build /web/packages/romd-admin-app/dist/ ./wwwroot/
COPY --from=frontend-build /out/admin-frontend.cdx.json /usr/share/romd/sbom/frontend.cdx.json
ENTRYPOINT ["dotnet", "romd-admin.dll"]

FROM runtime-${TARGETARCH} AS romd-consumer
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=backend-build /out/consumer/ ./
COPY --from=frontend-build /web/packages/romd-consumer-app/dist/ ./wwwroot/
COPY --from=frontend-build /out/consumer-frontend.cdx.json /usr/share/romd/sbom/frontend.cdx.json
ENTRYPOINT ["dotnet", "romd-consumer.dll"]

# Only worker images encode CHD, on both Linux architectures.
FROM catalog-runtime AS worker-runtime-amd64
ENV Romd__Native__ChdmanDirectory=/app/native/chdman

FROM catalog-runtime AS worker-runtime-arm64
ENV Romd__Native__ChdmanDirectory=/app/native/chdman

FROM worker-runtime-${TARGETARCH} AS romd-worker
COPY --from=backend-build /out/worker/ ./
# The existing bounded qualification commands run with this image's runtime and
# child payload. No mounted SDK, managed binaries, native library or tool is used.
COPY --from=backend-build /out/qualification/ /app/qualification/
COPY --from=chdman-notices / /usr/share/doc/romd/
# Only worker runtime stages name this directory for CHD encoding.
COPY --from=chdman-artifacts /build-manifest.json /app/native/chdman/build-manifest.json
COPY --from=chdman-artifacts /source-inputs.json /app/native/chdman/source-inputs.json
COPY --from=chdman-artifacts /native/chdman /app/native/chdman/native/chdman
# These checks run as the app user. chdman's usage text exits 1, so its banner is the check.
RUN test -x /usr/bin/prlimit \
    && test -x /usr/bin/nice \
    && test -f /app/media-worker/Romd.Media.Worker.dll \
    && test -f /app/media-worker/Romd.Media.Worker.runtimeconfig.json \
    && test -z "$(find /app/native \( ! -user root -o -perm /022 \) -print)" \
    && test -x /app/native/chdman/native/chdman \
    && ldd /app/native/chdman/native/chdman > /tmp/chdman-dependencies \
    && ! grep -q 'not found' /tmp/chdman-dependencies \
    && cat /tmp/chdman-dependencies \
    && rm /tmp/chdman-dependencies \
    && { /app/native/chdman/native/chdman help > /tmp/chdman-help || true; } \
    && grep -q '^chdman - MAME Compressed Hunks of Data (CHD) manager ' /tmp/chdman-help \
    && rm /tmp/chdman-help
ENTRYPOINT ["dotnet", "romd-worker.dll"]

# Retain PostgreSQL's upstream startup and tooling, service its runtime libraries,
# and run directly as postgres so the root-only privilege helper is unnecessary.
FROM docker.io/library/postgres:18-alpine@sha256:9ba53c7d41ea093baf5c1ba635ba5434c4f979880527d49bb7d9237d3193085b AS romd-postgres
ARG ROMD_SOURCE_REVISION=development
ARG ROMD_RELEASE_TAG=development
LABEL org.opencontainers.image.revision=$ROMD_SOURCE_REVISION org.opencontainers.image.version=$ROMD_RELEASE_TAG \
    org.opencontainers.image.source=https://github.com/MoonlarkStudios/romd org.opencontainers.image.licenses=PostgreSQL
RUN apk add --no-cache krb5-libs=1.22.3-r0 libxslt=1.1.43-r4 \
    && rm /usr/local/bin/gosu
USER postgres

FROM nginx:1.30.6-alpine-slim@sha256:e2355aa95529a7999dad94ddcb45a8768abf6fb0b204c93b1a3ffce477d7b893 AS romd-player
ARG ROMD_SOURCE_REVISION=development
ARG ROMD_RELEASE_TAG=development
LABEL org.opencontainers.image.revision=$ROMD_SOURCE_REVISION org.opencontainers.image.version=$ROMD_RELEASE_TAG \
    org.opencontainers.image.source=https://github.com/MoonlarkStudios/romd org.opencontainers.image.licenses=AGPL-3.0-only
COPY --from=notices /notices/ /usr/share/doc/romd/
EXPOSE 8080
COPY --from=player-build /web/packages/romd-player-app/dist/ /usr/share/nginx/html/
COPY --from=player-build /out/player-frontend.cdx.json /usr/share/romd/sbom/frontend.cdx.json
COPY --from=player-build /tmp/player-defaults.env /etc/romd/player-defaults.env
COPY --chmod=755 deploy/player/entrypoint.sh /docker-entrypoint.d/10-romd-player-config.sh
COPY deploy/player/default.conf.template /etc/romd/default.conf.template

FROM catalog-runtime AS romd-admin-dev
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=backend-build /out/admin/ ./
USER root
RUN mkdir -p wwwroot && printf '<!doctype html><html></html>' > wwwroot/index.html
USER $APP_UID
ENTRYPOINT ["dotnet", "romd-admin.dll"]

FROM runtime-${TARGETARCH} AS romd-consumer-dev
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=backend-build /out/consumer/ ./
USER root
RUN mkdir -p wwwroot && printf '<!doctype html><html></html>' > wwwroot/index.html
USER $APP_UID
ENTRYPOINT ["dotnet", "romd-consumer.dll"]
