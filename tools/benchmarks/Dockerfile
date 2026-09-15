FROM ubuntu:24.04

ARG RGBDS_VERSION=1.0.1

# Install system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates curl xz-utils git gcc make libpng-dev \
    time python3 xxd \
    && rm -rf /var/lib/apt/lists/*

# Install RGBDS (pinned version, prebuilt tarball for reproducibility)
RUN curl -fsSL "https://github.com/gbdev/rgbds/releases/download/v${RGBDS_VERSION}/rgbds-linux-x86_64.tar.xz" \
    | tar -xJ -C /usr/local/bin/

# Install .NET 10 runtime
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm /tmp/dotnet-install.sh

RUN mkdir -p /work /results /opt/koh

COPY run-benchmark.sh /usr/local/bin/run-benchmark.sh
COPY generate-reports.py /usr/local/bin/generate-reports.py
RUN chmod +x /usr/local/bin/run-benchmark.sh

WORKDIR /work

ENTRYPOINT ["run-benchmark.sh"]
