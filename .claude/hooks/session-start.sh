#!/bin/bash
set -euo pipefail

# Only run in remote (web) environments
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# --- .NET SDK 10.0 ---
DOTNET_INSTALL_DIR="/.dotnet"
if [ ! -x "$DOTNET_INSTALL_DIR/dotnet" ] || ! "$DOTNET_INSTALL_DIR/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_INSTALL_DIR"
  rm -f /tmp/dotnet-install.sh
fi

# Persist dotnet on PATH for the session
echo "export DOTNET_ROOT=$DOTNET_INSTALL_DIR" >> "$CLAUDE_ENV_FILE"
echo "export PATH=$DOTNET_INSTALL_DIR:\$PATH" >> "$CLAUDE_ENV_FILE"
export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
export PATH="$DOTNET_INSTALL_DIR:$PATH"

# Restore NuGet packages
dotnet restore "$CLAUDE_PROJECT_DIR/blade.slnx" --verbosity quiet

# --- FlexSpin (spin2cpp) ---
if ! command -v flexspin &>/dev/null; then
  apt-get update -qq
  apt-get install -y -qq build-essential bison

  SPIN2CPP_DIR="/tmp/spin2cpp"
  if [ ! -d "$SPIN2CPP_DIR" ]; then
    git clone --depth 1 https://github.com/totalspectrum/spin2cpp.git "$SPIN2CPP_DIR"
  fi

  make -C "$SPIN2CPP_DIR" -j"$(nproc)" -s
  cp "$SPIN2CPP_DIR/build/flexspin" /usr/local/bin/flexspin
  cp "$SPIN2CPP_DIR/build/flexcc" /usr/local/bin/flexcc
  cp "$SPIN2CPP_DIR/build/spin2cpp" /usr/local/bin/spin2cpp

  # Install include files
  mkdir -p /usr/local/lib/flexspin/include
  cp -r "$SPIN2CPP_DIR/include/"* /usr/local/lib/flexspin/include/
fi
