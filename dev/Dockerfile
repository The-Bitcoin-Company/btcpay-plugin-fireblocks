FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY global.json ./
COPY src ./src
COPY submodules/btcpayserver ./submodules/btcpayserver
RUN dotnet publish src/BTCPayServer.Plugins.Fireblocks/BTCPayServer.Plugins.Fireblocks.csproj \
    --configuration Release \
    --output /plugin \
    -p:ContinuousIntegrationBuild=true

CMD ["sh", "-c", "rm -rf /output/* && cp -a /plugin/. /output/"]
