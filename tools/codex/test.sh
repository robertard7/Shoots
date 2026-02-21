#!/usr/bin/env bash
set -euo pipefail

timeout 10m dotnet test -c Release
