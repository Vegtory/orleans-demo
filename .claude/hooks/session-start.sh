#!/bin/bash
# SessionStart hook: install toolchain so the backend can build/run in
# Claude Code on the web sessions. The base image ships Node/npm/Docker but
# not the .NET SDK, so we install it here. Idempotent and non-interactive.
set -euo pipefail

# Only run in the remote (web) environment; local machines manage their own SDKs.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# 1. Install the .NET 10 SDK if it isn't already present.
if ! command -v dotnet >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  SUDO=""
  if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi
  # Tolerate blocked third-party PPAs (e.g. deadsnakes/php) under the network
  # policy; the Ubuntu archive holds dotnet-sdk-10.0 and updates fine.
  $SUDO apt-get update -y || true
  $SUDO apt-get install -y dotnet-sdk-10.0
fi

dotnet --version || true

# 2. Install frontend dependencies (npm install benefits from layer caching).
if [ -d "$CLAUDE_PROJECT_DIR/frontend" ]; then
  npm install --prefix "$CLAUDE_PROJECT_DIR/frontend"
fi

# 3. Warm the NuGet restore cache for the backend.
if [ -f "$CLAUDE_PROJECT_DIR/src/App.Api/App.Api.csproj" ]; then
  dotnet restore "$CLAUDE_PROJECT_DIR/src/App.Api/App.Api.csproj"
fi
