FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS base

# Install RGBDS 1.0.1 (RGB9 revision 13) from GitHub release
ARG RGBDS_VERSION=1.0.1
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl xz-utils \
    && curl -fsSL "https://github.com/gbdev/rgbds/releases/download/v${RGBDS_VERSION}/rgbds-linux-x86_64.tar.xz" \
       | tar -xJ -C /usr/local/bin/ \
    && rgblink --version \
    && apt-get remove -y curl xz-utils && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet build tests/Koh.Compat.Tests/ -c Release --no-restore

ENTRYPOINT ["dotnet", "test", "--project", "tests/Koh.Compat.Tests/", "-c", "Release", "--no-build"]
