# The Weesite web app. Build from the repo root: docker build -f Containerfile -t weesite .
# Base images are pinned by digest; Dependabot (docker, T-0028) proposes updates.

# Build: restore first (cached while only code changes), then publish.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Weesite.Content/Weesite.Content.csproj src/Weesite.Content/
COPY src/Weesite.Search/Weesite.Search.csproj src/Weesite.Search/
COPY src/Weesite.ServiceDefaults/Weesite.ServiceDefaults.csproj src/Weesite.ServiceDefaults/
COPY src/Weesite.Web/Weesite.Web.csproj src/Weesite.Web/
RUN dotnet restore src/Weesite.Web/Weesite.Web.csproj
COPY src/Weesite.Content/ src/Weesite.Content/
COPY src/Weesite.Search/ src/Weesite.Search/
COPY src/Weesite.ServiceDefaults/ src/Weesite.ServiceDefaults/
COPY src/Weesite.Web/ src/Weesite.Web/
# Minify the scripts and stylesheets in place before publishing (T-0104), so the static assets it
# fingerprints and compresses are the minified ones; the repository keeps the readable sources.
# esbuild's linux-x64 npm package, pinned and checked by SHA-256 (npm's integrity hash matches).
ADD --checksum=sha256:9573bb2233aab0f9ea7647d5cca9726113cc1768de61d66b17267f4db84488f6 \
    https://registry.npmjs.org/@esbuild/linux-x64/-/linux-x64-0.28.2.tgz /tmp/esbuild.tgz
COPY scripts/minify.sh scripts/
RUN tar -xzf /tmp/esbuild.tgz -C /usr/local/bin --strip-components=2 package/bin/esbuild \
    && rm /tmp/esbuild.tgz \
    && scripts/minify.sh --in-place
RUN dotnet publish src/Weesite.Web/Weesite.Web.csproj --no-restore --configuration Release --output /app

# Run: chiseled Ubuntu (no shell, no package manager, non-root user); "extra" adds ICU and time
# zone data for localization.
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled-extra@sha256:cea9d5494f358fe43082300c24d1cd9f651066b6b7de1e5735bca8d3ade68649
WORKDIR /app
COPY --from=build /app ./
# The sample site: test loads it into its storage at startup (Seed__Path); prod never reads it.
COPY seed/ ./seed/
# The embedding model (ADR 0003): pinned to a Hugging Face commit and checked by SHA-256, the same
# files as scripts/fetch-model.sh.
ADD --chmod=444 --checksum=sha256:6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4 \
    https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/ea104dacec62c0de699686887e3f920caeb4f3e3/onnx/model_quantized.onnx \
    ./Models/bge-small-en-v1.5/model.onnx
ADD --chmod=444 --checksum=sha256:07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3 \
    https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/ea104dacec62c0de699686887e3f920caeb4f3e3/vocab.txt \
    ./Models/bge-small-en-v1.5/vocab.txt
ENV ASPNETCORE_HTTP_PORTS=8080
# The image has no Fontconfig configuration; this one points at the app's own fonts (Open Graph
# cards, src/Weesite.Content/Fonts/README.md).
ENV FONTCONFIG_FILE=/app/Fonts/fonts.conf
EXPOSE 8080
# $APP_UID is the base image's numeric user ID (1654); hadolint 2.15+ can't see through the variable.
# hadolint ignore=DL3066
USER $APP_UID
ENTRYPOINT ["dotnet", "Weesite.Web.dll"]
