#Requires -Version 7
<#
.SYNOPSIS
  Phase 1 acceptance tests: runs `behavediff run` against a throwaway git repo made from the
  stand-in app (src/Samples) and checks the five spec scenarios.

.DESCRIPTION
  1. Clean tree                       -> exit 0
  2. Response serialization regressed -> exit 1, TypeChanged on the response field
  3. DB default changed (no response) -> exit 1, unexpected db-write differences only
  4. Same as 3 with --expect entity   -> exit 1, all differences intended
  5. Build break in the working tree  -> exit 2, error names the working tree + compiler error

  The config lives outside the throwaway repo (exercises --config). Output: .spike-out/acceptance-<stamp>/.
#>
param([switch] $KeepRepo)

$ErrorActionPreference = 'Stop'
$stoneRoot = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $stoneRoot ".spike-out/acceptance-$stamp"
$repo = Join-Path ([IO.Path]::GetTempPath()) "behavediff-acceptance-$stamp"
New-Item -ItemType Directory -Force $out | Out-Null

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

Step 'Building behavediff'
$cliOut = Join-Path $out 'cli'
dotnet build (Join-Path $stoneRoot 'src/BehaveDiff.Cli/BehaveDiff.Cli.csproj') -c Release -o $cliOut -nodeReuse:false -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }
$cli = Join-Path $cliOut 'behavediff.dll'

Step "Creating throwaway repo $repo"
New-Item -ItemType Directory -Force $repo | Out-Null
Copy-Item -Recurse (Join-Path $stoneRoot 'src/Samples/SampleApi'), (Join-Path $stoneRoot 'src/Samples/SampleApi.Tests'), (Join-Path $stoneRoot 'global.json') $repo
Get-ChildItem $repo -Recurse -Directory -Include bin, obj | Remove-Item -Recurse -Force
Set-Content (Join-Path $repo '.gitignore') "bin/`nobj/`n.behavediff/`n"
git -C $repo init -q -b master
git -C $repo add -A
git -C $repo -c user.name=acceptance -c user.email=acceptance@example.invalid commit -q -m 'stand-in baseline'

# Config outside the repo.
$config = Join-Path $out 'behavediff.yml'
Set-Content $config @"
inputs:
  - kind: mstest
    project: SampleApi.Tests/SampleApi.Tests.csproj
selfNoiseCheck: true
"@

function Reset-Repo {
    git -C $repo checkout -q -- .
    git -C $repo clean -q -fd
}

function Edit-File([string] $rel, [string] $find, [string] $replace) {
    $path = Join-Path $repo $rel
    $text = Get-Content $path -Raw
    if (-not $text.Contains($find)) { throw "Edit target not found in ${rel}: $find" }
    Set-Content $path ($text.Replace($find, $replace)) -NoNewline
}

function Invoke-BehaveDiff([string] $name, [string[]] $extra) {
    $json = Join-Path $out "$name.json"
    $text = Join-Path $out "$name.txt"
    & dotnet $cli run --repo $repo --config $config --out $json @extra 2>&1 | Tee-Object -FilePath $text | Out-Host
    $exit = $LASTEXITCODE
    $report = Get-Content $json -Raw | ConvertFrom-Json -DateKind String
    return [pscustomobject] @{ Exit = $exit; Report = $report }
}

$results = [ordered] @{}
function Check([string] $scenario, [bool] $ok, [string] $detail) {
    $results[$scenario] = [pscustomobject] @{ Pass = $ok; Detail = $detail }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0}: {1}  {2}" -f $scenario, ($ok ? 'PASS' : 'FAIL'), $detail) -ForegroundColor $color
}

try {
    Step '1. Clean tree'
    $r = Invoke-BehaveDiff '1-clean' @()
    Check '1-clean' ($r.Exit -eq 0 -and $r.Report.differences.Count -eq 0) "exit=$($r.Exit) differences=$($r.Report.differences.Count) noise=$($r.Report.noise.Count)"

    Step '2. Response serialization regression (tags array -> comma-separated string)'
    Reset-Repo
    Edit-File 'SampleApi/Controllers/WidgetsController.cs' 'string[] Tags, bool IsArchived' 'string Tags, bool IsArchived'
    Edit-File 'SampleApi/Controllers/WidgetsController.cs' "w.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)" 'w.Tags'
    $r = Invoke-BehaveDiff '2-serialization' @()
    $tags = @($r.Report.differences | Where-Object { $_.path -eq '$.body.tags' -and $_.kind -eq 'TypeChanged' -and $_.observationKind -eq 'http-response' })
    $failed = @($r.Report.differences | Where-Object { $_.observationKind -eq 'test-result' })
    Check '2-serialization' ($r.Exit -eq 1 -and $tags.Count -gt 0) "exit=$($r.Exit) tags TypeChanged=$($tags.Count) triggers=$((($tags.trigger | Sort-Object -Unique) -join ', ')) test-result changes=$($failed.Count)"

    Step '3. DB write changed, no response changed (Priority default Normal -> Low)'
    Reset-Repo
    Edit-File 'SampleApi/Data/CatalogContext.cs' 'Priority { get; set; } = WidgetPriority.Normal;' 'Priority { get; set; } = WidgetPriority.Low;'
    $r = Invoke-BehaveDiff '3-db-write' @()
    $db = @($r.Report.differences | Where-Object { $_.observationKind -eq 'db-write' -and $_.path -eq '$.values.Priority' -and $_.classification -eq 'Unexpected' })
    $other = @($r.Report.differences | Where-Object { $_.observationKind -ne 'db-write' })
    $passed = $r.Report.run.current.passed -eq $r.Report.run.current.tests
    Check '3-db-write' ($r.Exit -eq 1 -and $db.Count -gt 0 -and $other.Count -eq 0 -and $passed) "exit=$($r.Exit) unexpected Priority diffs=$($db.Count) non-db diffs=$($other.Count) all tests passed=$passed"

    Step '4. Same as 3 with --expect entity:Widget'
    $r = Invoke-BehaveDiff '4-expect' @('--intent', 'Default new widgets to Low priority', '--expect', 'entity:Widget')
    $s = $r.Report.summary
    Check '4-expect' ($r.Exit -eq 1 -and $s.unexpected -eq 0 -and $s.intended -gt 0) "exit=$($r.Exit) unexpected=$($s.unexpected) intended=$($s.intended)"

    Step '5. Build break in the working tree'
    Reset-Repo
    Set-Content (Join-Path $repo 'SampleApi/Broken.cs') 'namespace SampleApi; class Broken { int x = "not an int"; }'
    $r = Invoke-BehaveDiff '5-build-break' @()
    $e = $r.Report.errors | Select-Object -First 1
    Check '5-build-break' ($r.Exit -eq 2 -and $e.tree -eq 'current' -and $e.stage -eq 'build' -and $e.message -match 'working tree' -and $e.detail -match 'CS0029') "exit=$($r.Exit) error=[$($e.tree)/$($e.stage)] $($e.message) | $(($e.detail -split "`n")[0])"
}
finally {
    $results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out 'acceptance-summary.json')
    if (-not $KeepRepo) { Remove-Item -Recurse -Force $repo -ErrorAction SilentlyContinue }
}

$failedCount = @($results.Values | Where-Object { -not $_.Pass }).Count
Step "Acceptance: $($results.Count - $failedCount)/$($results.Count) passed. Output: $out"
exit ($failedCount -eq 0 ? 0 : 1)
