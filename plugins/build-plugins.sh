#!/usr/bin/env bash
# Builds the script plugins and lays them out FLAT in plugins/out/ — the directory the runtime is
# pointed at with Scripting__Sandbox__PluginDirectory (see tests/Core.IntegrationTests/Tests/ScriptPluginLab/README.md).
#
# Copies only what the runtime does not already ship: the plugin assembly and its third-party
# dependencies. Npgsql stays out on purpose — the host has it, and a host assembly always resolves
# to the host's copy.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/out"
publish="$here/ScriptPluginLab.Sql/bin/publish"

dotnet publish "$here/ScriptPluginLab.Sql/ScriptPluginLab.Sql.csproj" -c Release -o "$publish" --nologo -v quiet

mkdir -p "$out"
for dll in ScriptPluginLab.Sql.dll Dapper.dll; do
  cp "$publish/$dll" "$out/$dll"
done

echo "plugins ready in $out:"
ls -1 "$out"
