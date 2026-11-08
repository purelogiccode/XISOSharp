# syntax=docker/dockerfile:1
#
# Container image for the XISOSharp CLI.
#
#   docker build -t xisosharp --build-arg VERSION=1.9.0 .
#   docker run --rm -v "$PWD:/work" -w /work xisosharp -l game.iso
#
# The image is framework-dependent on the .NET 10 runtime and contains the
# CLI only; the Avalonia GUI is desktop-only and ships in the RID bundles.

ARG DOTNET_SDK=10.0
ARG DOTNET_RUNTIME=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
ARG VERSION=0.0.0-local
WORKDIR /src

# Restore first so the expensive layer is cached across source-only changes.
COPY Directory.Build.props NuGet.config .editorconfig ./
COPY XISOSharp/XISOSharp.csproj XISOSharp/
COPY XISOSharp.Cli/XISOSharp.Cli.csproj XISOSharp.Cli/
RUN dotnet restore XISOSharp.Cli/XISOSharp.Cli.csproj -r linux-x64

COPY XISOSharp/ XISOSharp/
COPY XISOSharp.Cli/ XISOSharp.Cli/
RUN dotnet publish XISOSharp.Cli/XISOSharp.Cli.csproj \
        -c Release -f net10.0 -r linux-x64 --no-restore \
        --self-contained false \
        -p:PublishSingleFile=false -p:PublishTrimmed=false \
        -p:MinVerVersionOverride="${VERSION}" \
        -o /out

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_RUNTIME} AS runtime
ARG VERSION=0.0.0-local

LABEL org.opencontainers.image.title="XISOSharp"
LABEL org.opencontainers.image.description="Create, extract, list, rewrite and archive Xbox XISO/XDVDFS disc images."
LABEL org.opencontainers.image.source="https://github.com/purelogiccode/XISOSharp"
LABEL org.opencontainers.image.licenses="MIT"
LABEL org.opencontainers.image.version="${VERSION}"

ENV DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    XISO_NO_UPDATE_CHECK=1

WORKDIR /opt/xisosharp
COPY --from=build /out/ ./
RUN ln -s /opt/xisosharp/XISOSharp /usr/local/bin/xisosharp

ENTRYPOINT ["/opt/xisosharp/XISOSharp"]
