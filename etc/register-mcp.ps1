#Requires -Version 7
<#
.SYNOPSIS
  Registers `behavediff mcp` for one project only (Claude Code "local" scope) by editing ~/.claude.json.
  Works for the Claude desktop app's Code tab and the CLI, which read the same file.

.DESCRIPTION
  ~/.claude.json keeps per-project settings under "projects", and the same folder can appear under two
  spellings (C:\x\y and C:/x/y). This adds the server to every entry for the project (creating one if
  none exists) and leaves everything else untouched:
    - refuses to run while Claude is running (it rewrites this file), unless -Force
    - backs the file up first, validates the result, and restores the backup on any failure
    - parses with System.Text.Json, so strings, numbers and dates round-trip unchanged

.EXAMPLE
  pwsh -File etc/register-mcp.ps1 -ProjectPath C:\src\my-app -Config C:\Users\me\behavediff-my-app.yml
#>
param(
    [Parameter(Mandatory)] [string] $ProjectPath,
    # Config file for that project, ideally stored outside the repo. Optional.
    [string] $Config,
    [string] $ExePath = (Join-Path $env:USERPROFILE '.dotnet\tools\behavediff.exe'),
    [string] $ClaudeJson = (Join-Path ($env:CLAUDE_CONFIG_DIR ?? $env:USERPROFILE) '.claude.json'),
    [string] $Name = 'behavediff',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Text.Json

if (-not $Force -and (Get-Process -Name 'Claude' -ErrorAction SilentlyContinue)) {
    throw 'Claude is running and may overwrite this file. Quit the Claude desktop app (including the tray icon) and any claude CLI sessions, then run again. (-Force skips this check.)'
}
if (-not (Test-Path $ClaudeJson)) { throw "Not found: $ClaudeJson" }
if (-not (Test-Path $ExePath)) { Write-Warning "behavediff not found at $ExePath. Install it first (see docs/agent-instructions.md); registering anyway." }
if ($Config -and -not (Test-Path $Config)) { Write-Warning "Config not found: $Config. Registering anyway." }

$project = [IO.Path]::GetFullPath($ProjectPath).TrimEnd('\', '/')
$spellings = @($project.Replace('/', '\'), $project.Replace('\', '/')) | Select-Object -Unique

$serverArgs = @('mcp', '--repo', $project)
if ($Config) { $serverArgs += @('--config', [IO.Path]::GetFullPath($Config)) }
$serverJson = [ordered] @{ type = 'stdio'; command = $ExePath; args = $serverArgs } | ConvertTo-Json -Compress

$original = [IO.File]::ReadAllText($ClaudeJson)
$root = [System.Text.Json.Nodes.JsonNode]::Parse($original).AsObject()
if (-not $root.ContainsKey('projects')) { $root['projects'] = [System.Text.Json.Nodes.JsonObject]::new() }
$projects = $root['projects'].AsObject()

# Every existing key for this folder, matched case-insensitively (Windows paths).
$keys = @($projects | ForEach-Object { $_.Key } | Where-Object { $k = $_; $spellings | Where-Object { $_ -ieq $k } })
if ($keys.Count -eq 0) {
    $keys = $spellings
    foreach ($k in $keys) { $projects[$k] = [System.Text.Json.Nodes.JsonObject]::new() }
}

foreach ($k in $keys) {
    $entry = $projects[$k].AsObject()
    if (-not $entry.ContainsKey('mcpServers') -or $null -eq $entry['mcpServers']) {
        $entry['mcpServers'] = [System.Text.Json.Nodes.JsonObject]::new()
    }
    $servers = $entry['mcpServers'].AsObject()
    $action = if ($servers.ContainsKey($Name)) { 'updated' } else { 'added' }
    $servers[$Name] = [System.Text.Json.Nodes.JsonNode]::Parse($serverJson)
    Write-Host "  $action '$Name' for project key: $k"
}

$options = [System.Text.Json.JsonSerializerOptions]::new()
$options.WriteIndented = $true
$options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
$updated = $root.ToJsonString($options)
if (-not $original.Contains("`r`n")) { $updated = $updated.Replace("`r`n", "`n") }
if ($original.EndsWith("`n") -and -not $updated.EndsWith("`n")) { $updated += $original.Contains("`r`n") ? "`r`n" : "`n" }
[System.Text.Json.Nodes.JsonNode]::Parse($updated) | Out-Null   # validate before touching the file

$backup = "$ClaudeJson.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Copy-Item $ClaudeJson $backup
try {
    [IO.File]::WriteAllText($ClaudeJson, $updated, [Text.UTF8Encoding]::new($false))
    [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($ClaudeJson)) | Out-Null
}
catch {
    Copy-Item $backup $ClaudeJson -Force
    throw "Write failed; restored the backup. $_"
}

Write-Host "Done. Backup: $backup"
Write-Host "Start Claude, open a session on $project, and ask which MCP servers are available."
