#Requires -Version 7
<#
.SYNOPSIS
  Phase 0 capture spike: run a target repo's MSTest/MTP test project with the BehaveDiff
  startup hook injected, then attribute observations to tests and summarize.

.DESCRIPTION
  Never modifies -RepoPath. The repo is cloned (from its local path, HEAD of the current
  branch) into a temp directory; gitignored appsettings*.json files are copied in from the
  original so the build/test has the same local config. Everything runs in the clone.

  Output lands in -OutDir (default: <this repo>/.spike-out/<name>-<timestamp>) plus a .zip of it.
  That directory is gitignored: observations can contain real response data.

.EXAMPLE
  # On the WhatInBox machine (Docker running):
  ./etc/spike.ps1 -RepoPath C:\Users\Ryan\source\repos\WhatInBox `
                  -TestProject src/Tests/WhatInBox.Tests.Regression/WhatInBox.Tests.Regression.csproj

.EXAMPLE
  # Locally, against the stand-in (no clone needed, it lives in this repo):
  ./etc/spike.ps1 -TestProject src/Samples/SampleApi.Tests/SampleApi.Tests.csproj
#>
param(
    # Repo to test. Omit to use this repo (the stand-in sample) in place.
    [string] $RepoPath,
    # Test project path, relative to the repo root.
    [Parameter(Mandatory)] [string] $TestProject,
    [string] $OutDir,
    # Keep the temp clone for inspection instead of deleting it.
    [switch] $KeepClone
)

$ErrorActionPreference = 'Stop'
$stoneRoot = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$name = if ($RepoPath) { Split-Path $RepoPath -Leaf } else { 'stand-in' }
if (-not $OutDir) { $OutDir = Join-Path $stoneRoot ".spike-out/$name-$stamp" }
$OutDir = [IO.Path]::GetFullPath($OutDir)
$captureDir = Join-Path $OutDir 'capture'
$trxDir = Join-Path $OutDir 'trx'
New-Item -ItemType Directory -Force $captureDir, $trxDir | Out-Null

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Invoke-Checked([string] $what, [scriptblock] $cmd, [string] $logFile) {
    & $cmd *>&1 | Tee-Object -FilePath $logFile | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE). Log: $logFile" }
}

$clone = $null
try {
    # 1. Build the hook from this repo.
    Step 'Building BehaveDiff.Capture'
    $hookProj = Join-Path $stoneRoot 'src/BehaveDiff.Capture/BehaveDiff.Capture.csproj'
    $hookOut = Join-Path $OutDir 'hook'
    Invoke-Checked 'Hook build' { dotnet build $hookProj -c Release -o $hookOut -nodeReuse:false -nologo -v q } (Join-Path $OutDir 'build-hook.log')
    $hookDll = Join-Path $hookOut 'BehaveDiff.Capture.dll'

    # 2. Clone the target (read-only use of the original).
    if ($RepoPath) {
        $RepoPath = (Resolve-Path $RepoPath).Path
        $clone = Join-Path ([IO.Path]::GetTempPath()) "behavediff-spike-$stamp"
        Step "Cloning $RepoPath -> $clone"
        git clone --quiet --no-hardlinks $RepoPath $clone
        if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }

        # Gitignored local config the build/tests may need.
        $ignored = git -C $RepoPath ls-files --others --ignored --exclude-standard |
            Where-Object { (Split-Path $_ -Leaf) -like 'appsettings*.json' }
        foreach ($rel in $ignored) {
            $dest = Join-Path $clone $rel
            New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null
            Copy-Item (Join-Path $RepoPath $rel) $dest
            Write-Host "    copied untracked config: $rel"
        }
        $root = $clone
        $commit = git -C $clone rev-parse HEAD
    } else {
        $root = $stoneRoot
        $commit = git -C $stoneRoot rev-parse HEAD 2>$null
    }
    $proj = Join-Path $root $TestProject

    # 3. Build the target WITHOUT the hook env vars, so MSBuild processes never see them.
    Step 'Building test project'
    Invoke-Checked 'Target build' { dotnet build $proj -nodeReuse:false -nologo -v q } (Join-Path $OutDir 'build-target.log')

    # 4. Run tests with capture injected.
    Step 'Running tests with capture'
    $env:DOTNET_STARTUP_HOOKS = $hookDll
    $env:BEHAVEDIFF_CAPTURE_DIR = $captureDir
    try {
        & dotnet test --project $proj --no-build --report-trx --results-directory $trxDir *>&1 |
            Tee-Object -FilePath (Join-Path $OutDir 'test.log') | Out-Host
        $testExit = $LASTEXITCODE
    } finally {
        Remove-Item Env:DOTNET_STARTUP_HOOKS, Env:BEHAVEDIFF_CAPTURE_DIR -ErrorAction SilentlyContinue
    }

    # 5. Attribute observations to tests by TRX time windows (tests run sequentially).
    Step 'Analyzing'
    $trxFile = Get-ChildItem $trxDir -Filter *.trx -Recurse | Select-Object -First 1
    $tests = @()
    if ($trxFile) {
        [xml] $trx = Get-Content $trxFile.FullName -Raw
        $tests = @($trx.TestRun.Results.UnitTestResult | ForEach-Object {
            [pscustomobject] @{
                Name    = $_.testName
                Outcome = $_.outcome
                Start   = [DateTimeOffset]::Parse($_.startTime)
                End     = [DateTimeOffset]::Parse($_.endTime)
            }
        } | Sort-Object Start)
    }

    $observations = @(Get-ChildItem $captureDir -Filter 'observations-*.jsonl' | ForEach-Object {
        Get-Content $_.FullName | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable }
    })

    $attributed = foreach ($o in $observations) {
        # ConvertFrom-Json turns ISO strings into DateTime already; don't round-trip via string.
        $ts = if ($o.ts -is [datetime]) { [DateTimeOffset] $o.ts.ToUniversalTime() } else { [DateTimeOffset]::Parse($o.ts) }
        $hit = $tests | Where-Object { $_.Start -le $ts -and $ts -le $_.End } | Select-Object -First 1
        $o['testByTrx'] = ${hit}?.Name
        $o
    }
    $attributedPath = Join-Path $OutDir 'observations-attributed.jsonl'
    $attributed | ForEach-Object { $_ | ConvertTo-Json -Depth 64 -Compress } | Set-Content $attributedPath

    # Stack attribution names "Class.Method"; TRX names may be "Method" or fully qualified.
    $agree = @($attributed | Where-Object { $_.test -and $_.testByTrx -and ($_.test -split '\.')[-1] -eq ($_.testByTrx -split '\.')[-1] }).Count
    $disagree = @($attributed | Where-Object { $_.test -and $_.testByTrx -and ($_.test -split '\.')[-1] -ne ($_.testByTrx -split '\.')[-1] }).Count

    $summary = [ordered] @{
        repo                = $name
        commit              = $commit
        testProject         = $TestProject
        testExitCode        = $testExit
        machine             = $env:COMPUTERNAME
        dotnetSdk           = (dotnet --version)
        hookProcesses       = @(Get-ChildItem $captureDir -Filter 'hook-*.log' | ForEach-Object { (Get-Content $_.FullName -TotalCount 1) })
        hookErrors          = @(Get-ChildItem $captureDir -Filter 'hook-*.log' | ForEach-Object { Select-String -Path $_.FullName -Pattern 'ERROR in' } | ForEach-Object { $_.Line }) | Select-Object -Unique
        testsInTrx          = $tests.Count
        observations        = $observations.Count
        byKind              = $attributed | Group-Object { $_.kind } | ForEach-Object { @{ $_.Name = $_.Count } }
        attribution         = [ordered] @{
            stack                = @($attributed | Where-Object { $_.test }).Count
            trx                  = @($attributed | Where-Object { $_.testByTrx }).Count
            neither              = @($attributed | Where-Object { -not $_.test -and -not $_.testByTrx }).Count
            stackAndTrxAgree     = $agree
            stackAndTrxDisagree  = $disagree
        }
        perTest             = $tests | ForEach-Object {
            $t = $_
            $mine = @($attributed | Where-Object { $_.testByTrx -eq $t.Name })
            [ordered] @{
                test         = $t.Name
                outcome      = $t.Outcome
                httpResponse = @($mine | Where-Object { $_.kind -eq 'http-response' }).Count
                dbWrite      = @($mine | Where-Object { $_.kind -eq 'db-write' }).Count
            }
        }
        unattributed        = @($attributed | Where-Object { -not $_.testByTrx } | ForEach-Object { "$($_.seq) $($_.kind) $($_.trigger)" })
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir 'summary.json')

    $zip = "$OutDir.zip"
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zip -Force

    Step 'Done'
    Write-Host "  tests in TRX:     $($tests.Count)  (dotnet test exit $testExit)"
    Write-Host "  observations:     $($observations.Count)"
    Write-Host "  attributed (TRX): $($summary.attribution.trx)   (stack): $($summary.attribution.stack)   neither: $($summary.attribution.neither)"
    Write-Host "  hook errors:      $(@($summary.hookErrors).Count)"
    Write-Host "  output:           $OutDir"
    Write-Host "  bundle:           $zip"
}
finally {
    if ($clone -and -not $KeepClone -and (Test-Path $clone)) {
        Step "Removing clone $clone"
        Remove-Item -Recurse -Force $clone -ErrorAction SilentlyContinue
    }
}
