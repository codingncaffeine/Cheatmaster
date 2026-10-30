#!/usr/bin/env bash
# Builds Cheatmaster from this checkout and starts it. Needs the .NET 10 SDK.
set -euo pipefail
cd "$(dirname "$0")"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_NOLOGO=1
[ -f scratch/env.sh ] && source scratch/env.sh
dotnet build src/Cheatmaster.App/Cheatmaster.App.csproj -c Release -v q --nologo
exec src/Cheatmaster.App/bin/Release/net10.0/Cheatmaster "$@"
