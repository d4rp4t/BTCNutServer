FROM mcr.microsoft.com/dotnet/sdk:8.0 AS builder

RUN apt-get update && apt-get install -y --no-install-recommends chromium-driver \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /source

# Copy csproj files first for layer caching during restore
COPY submodules/btcpayserver/Build/Common.csproj        submodules/btcpayserver/Build/Common.csproj
COPY submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj       submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj
COPY submodules/btcpayserver/BTCPayServer.Common/BTCPayServer.Common.csproj        submodules/btcpayserver/BTCPayServer.Common/BTCPayServer.Common.csproj
COPY submodules/btcpayserver/BTCPayServer.Rating/BTCPayServer.Rating.csproj        submodules/btcpayserver/BTCPayServer.Rating/BTCPayServer.Rating.csproj
COPY submodules/btcpayserver/BTCPayServer.Data/BTCPayServer.Data.csproj         submodules/btcpayserver/BTCPayServer.Data/BTCPayServer.Data.csproj
COPY submodules/btcpayserver/BTCPayServer.Abstractions/BTCPayServer.Abstractions.csproj         submodules/btcpayserver/BTCPayServer.Abstractions/BTCPayServer.Abstractions.csproj
COPY submodules/btcpayserver/BTCPayServer.Client/BTCPayServer.Client.csproj         submodules/btcpayserver/BTCPayServer.Client/BTCPayServer.Client.csproj
COPY submodules/btcpayserver/BTCPayServer.Tests/BTCPayServer.Tests.csproj         submodules/btcpayserver/BTCPayServer.Tests/BTCPayServer.Tests.csproj
COPY submodules/DotNut/DotNut/DotNut.csproj         submodules/DotNut/DotNut/DotNut.csproj
COPY Plugin/BTCPayServer.Plugins.Cashu/BTCPayServer.Plugins.Cashu.csproj        Plugin/BTCPayServer.Plugins.Cashu/BTCPayServer.Plugins.Cashu.csproj
COPY BTCPayserver.Plugins.Cashu.Tests/BTCPayserver.Plugins.Cashu.Tests.csproj       BTCPayserver.Plugins.Cashu.Tests/BTCPayserver.Plugins.Cashu.Tests.csproj

RUN cd BTCPayserver.Plugins.Cashu.Tests && dotnet restore

# Copy full source
COPY submodules/btcpayserver/btcpayserver.sln  submodules/btcpayserver/btcpayserver.sln
COPY submodules/btcpayserver/Build/.           submodules/btcpayserver/Build/.
COPY submodules/btcpayserver/BTCPayServer.Common/.      submodules/btcpayserver/BTCPayServer.Common/.
COPY submodules/btcpayserver/BTCPayServer.Rating/.      submodules/btcpayserver/BTCPayServer.Rating/.
COPY submodules/btcpayserver/BTCPayServer.Data/.        submodules/btcpayserver/BTCPayServer.Data/.
COPY submodules/btcpayserver/BTCPayServer.Abstractions/.        submodules/btcpayserver/BTCPayServer.Abstractions/.
COPY submodules/btcpayserver/BTCPayServer.Client/.      submodules/btcpayserver/BTCPayServer.Client/.
COPY submodules/btcpayserver/BTCPayServer/.    submodules/btcpayserver/BTCPayServer/.
COPY submodules/btcpayserver/BTCPayServer.Tests/.    submodules/btcpayserver/BTCPayServer.Tests/.
COPY submodules/DotNut/DotNut/.      submodules/DotNut/DotNut/.
COPY Plugin/BTCPayServer.Plugins.Cashu/.      Plugin/BTCPayServer.Plugins.Cashu/.
COPY BTCPayserver.Plugins.Cashu.Tests/.      BTCPayserver.Plugins.Cashu.Tests/.

ARG CONFIGURATION_NAME=Release
RUN cd BTCPayserver.Plugins.Cashu.Tests && dotnet build --configuration ${CONFIGURATION_NAME} -maxcpucount:1 /p:BuildInParallel=false

# Install the Cashu plugin so BTCPay's PluginManager finds it at /root/.btcpayserver/Plugins
RUN PLUGIN_DIR=/root/.btcpayserver/Plugins/BTCPayServer.Plugins.Cashu && \
    mkdir -p "$PLUGIN_DIR" && \
    cp /source/Plugin/BTCPayServer.Plugins.Cashu/bin/${CONFIGURATION_NAME}/net*/BTCPayServer.Plugins.Cashu.dll \
       "$PLUGIN_DIR/BTCPayServer.Plugins.Cashu.dll"

RUN dotnet tool install --global Microsoft.Playwright.CLI
ENV PATH="$PATH:/root/.dotnet/tools"

WORKDIR /source/BTCPayserver.Plugins.Cashu.Tests
RUN playwright install chromium --with-deps
ENV CONFIGURATION_NAME=${CONFIGURATION_NAME}
ENTRYPOINT ["./docker-entrypoint.sh"]
