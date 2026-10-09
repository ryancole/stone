#Requires -Version 7
<#
.SYNOPSIS
  Phase 2 check: drives `behavediff mcp` with a real MCP client (etc/mcp-smoke.cs) against a
  throwaway repo made from the stand-in, with a working-tree change to a DB default.
  Covers list_tools, check_behavior_changes, explain_difference, accept_difference, and re-check.
#>
param([switch] $KeepRepo)

$ErrorActionPreference = 'Stop'
$stoneRoot = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $stoneRoot ".spike-out/mcp-smoke-$stamp"
$repo = Join-Path ([IO.Path]::GetTempPath()) "behavediff-mcp-$stamp"
New-Item -ItemType Directory -Force $out, $repo | Out-Null
function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

Step 'Building behavediff'
$cliOut = Join-Path $out 'cli'
dotnet build (Join-Path $stoneRoot 'src/BehaveDiff.Cli/BehaveDiff.Cli.csproj') -c Release -o $cliOut -nodeReuse:false -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

Step "Creating throwaway repo $repo"
Copy-Item -Recurse (Join-Path $stoneRoot 'src/Samples/SampleApi'), (Join-Path $stoneRoot 'src/Samples/SampleApi.Tests'), (Join-Path $stoneRoot 'global.json') $repo
Get-ChildItem $repo -Recurse -Directory -Include bin, obj | Remove-Item -Recurse -Force
Set-Content (Join-Path $repo '.gitignore') "bin/`nobj/`n.behavediff/`n"
Set-Content (Join-Path $repo '.behavediff.yml') "inputs:`n  - kind: mstest`n    project: SampleApi.Tests/SampleApi.Tests.csproj`nselfNoiseCheck: false`n"
git -C $repo init -q -b master
git -C $repo add -A
git -C $repo -c user.name=smoke -c user.email=smoke@example.invalid commit -q -m 'stand-in baseline'

$file = Join-Path $repo 'SampleApi/Data/CatalogContext.cs'
(Get-Content $file -Raw).Replace('= WidgetPriority.Normal;', '= WidgetPriority.Low;') | Set-Content $file -NoNewline

try {
    Step 'Running MCP client'
    dotnet run (Join-Path $PSScriptRoot 'mcp-smoke.cs') -- (Join-Path $cliOut 'behavediff.dll') $repo 2>&1 |
        Tee-Object -FilePath (Join-Path $out 'mcp-smoke.txt') | Out-Host
    $exit = $LASTEXITCODE
}
finally {
    if (-not $KeepRepo) { Remove-Item -Recurse -Force $repo -ErrorAction SilentlyContinue }
}
Step "Exit $exit. Output: $out"
exit $exit
