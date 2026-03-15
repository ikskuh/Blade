#!/usr/bin/env bash
set -euo pipefail

DOTNET_CHANNEL="10.0"
DOTNET_INSTALL_DIR="$HOME/.dotnet"
DOTNET_BIN="$DOTNET_INSTALL_DIR/dotnet"
PROFILE_SNIPPET='export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"'

# Install curl if the image doesn't already have it.
if ! command -v curl >/dev/null 2>&1; then
  apt-get update
  apt-get install -y curl
fi

mkdir -p "$DOTNET_INSTALL_DIR"

# Install the latest SDK in the .NET 10 channel into ~/.dotnet
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh \
  --channel "$DOTNET_CHANNEL" \
  --install-dir "$DOTNET_INSTALL_DIR"

# Make dotnet available now
export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

# Make dotnet available in future shells
grep -qxF 'export DOTNET_ROOT="$HOME/.dotnet"' "$HOME/.bashrc" 2>/dev/null || \
  printf '\n%s\n' "$PROFILE_SNIPPET" >> "$HOME/.bashrc"

grep -qxF 'export DOTNET_ROOT="$HOME/.dotnet"' "$HOME/.profile" 2>/dev/null || \
  printf '\n%s\n' "$PROFILE_SNIPPET" >> "$HOME/.profile"

# Verify installation
"$DOTNET_BIN" --info
"$DOTNET_BIN" --list-sdks
