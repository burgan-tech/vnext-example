#!/usr/bin/env bash
# Builds the script plugins and lays them out FLAT in plugins/out/ — the directory the runtime is
# pointed at with Scripting__Sandbox__PluginDirectory (see tests/Core.IntegrationTests/Tests/ScriptPluginLab/README.md).
#
# ScriptPluginLab.Sql: copies only what the runtime does not already ship — the plugin assembly and
# Dapper. Npgsql stays out on purpose: the host has it, and a host assembly always resolves to the
# host's copy.
#
# Microsoft.Data.SqlClient + Dapper (ScriptPluginLab.SqlServer carrier): copies the WHOLE published
# dependency chain, the way an operator mounts a driver the runtime does not ship. Published for a Unix
# RID so SqlClient comes from runtimes/unix/lib/ (the plain lib/ copy is a PlatformNotSupportedException
# stub). The managed Unix build is the same for linux-x64, linux-musl-x64 and osx, so one out/ serves
# both the local hosts and the Alpine images.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/out"
publish="$here/ScriptPluginLab.Sql/bin/publish"
sqlserver_publish="$here/ScriptPluginLab.SqlServer/bin/publish"

dotnet publish "$here/ScriptPluginLab.Sql/ScriptPluginLab.Sql.csproj" -c Release -o "$publish" --nologo -v quiet
dotnet publish "$here/ScriptPluginLab.SqlServer/ScriptPluginLab.SqlServer.csproj" -c Release -r linux-x64 \
  -o "$sqlserver_publish" --nologo -v quiet

mkdir -p "$out"
for dll in ScriptPluginLab.Sql.dll Dapper.dll; do
  cp "$publish/$dll" "$out/$dll"
done
for dll in "$sqlserver_publish"/*.dll; do
  [ "$(basename "$dll")" = "ScriptPluginLab.SqlServer.dll" ] && continue
  cp "$dll" "$out/"
done

echo "plugins ready in $out:"
ls -1 "$out"
