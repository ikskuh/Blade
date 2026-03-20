FROM ghcr.io/openai/codex-universal

COPY setup-dotnet.sh /root/setup-dotnet.sh
RUN bash /root/setup-dotnet.sh

COPY setup-flexspin.sh /root/setup-flexspin.sh
RUN bash /root/setup-flexspin.sh

# Disable dotnet telemetry:
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

ENV DOTNET_ROOT="/root/.dotnet"
ENV PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

# Install the dotnet reportgenerator globally:
RUN dotnet tool install -g dotnet-reportgenerator-globaltool
