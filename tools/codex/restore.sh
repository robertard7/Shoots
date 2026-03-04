#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  export DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"
  mkdir -p "$DOTNET_INSTALL_DIR"

  if [ ! -x "$DOTNET_INSTALL_DIR/dotnet" ]; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$DOTNET_INSTALL_DIR"
  fi

  export PATH="$DOTNET_INSTALL_DIR:$PATH"
fi

dotnet --info
dotnet restore Shoots.sln
