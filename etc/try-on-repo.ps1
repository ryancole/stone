#Requires -Version 7
<#
.SYNOPSIS
  Run `behavediff run` against a disposable clone of any repo, optionally after applying edits.

.DESCRIPTION
  Never modifies -RepoPath: it is cloned (current branch, committed state) into a temp directory;
  gitignored appsettings*.json files are copied in. Edits are applied to the clone's working tree,
  so the run compares "clone HEAD" (base) against "clone HEAD + edits" (working tree).
  The config is written outside the clone (exercises --config). Output: .spike-out/try-<stamp>/.

.EXAMPLE
  # Clean tree: expect exit 0.
  ./etc/try-on-repo.ps1 -RepoPath C:\path\to\app -TestProject tests/App.Tests/App.Tests.csproj

.EXAMPLE
  # A change: each -Edit is "relative/path.cs::find text::replacement text" (exact, first-and-only match required).
  ./etc/try-on-repo.ps1 -RepoPath C:\path\to\app -TestProject tests/App.Tests/App.Tests.csproj `
    -Edit 'src/Api/Widget.cs::IsArchived { get; set; }::IsArchived { get; set; } = true;' -Expect 'entity:Widget'
#>
param(
    [Parameter(Mandatory)] [string] $RepoPath,
    [Parameter(Mandatory)] [string] $TestProject,
    [string[]] $Edit = @(),
    [string[]] $Expect = @(),
    [string] $Intent,
    [switch] $NoSelfNoise,
    [switch] $KeepClone
)

$ErrorActionPreference = 'Stop'
$stoneRoot = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $stoneRoot ".spike-out/try-$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

Step 'Building behavediff'
$cliOut = Join-Path $out 'cli'
dotnet build (Join-Path $stoneRoot 'src/BehaveDiff.Cli/BehaveDiff.Cli.csproj') -c Release -o $cliOut -nodeReuse:false -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

$RepoPath = (Resolve-Path $RepoPath).Path
$clone = Join-Path ([IO.Path]::GetTempPath()) "behavediff-try-$stamp"
Step "Cloning $RepoPath -> $clone"
git clone --quiet --no-hardlinks $RepoPath $clone
if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }

try {
    git -C $RepoPath ls-files --others --ignored --exclude-standard |
        Where-Object { (Split-Path $_ -Leaf) -like 'appsettings*.json' -and $_ -notmatch '(^|/)(bin|obj)/' } |
        ForEach-Object {
            $dest = Join-Path $clone $_
            New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null
            Copy-Item (Join-Path $RepoPath $_) $dest
            Write-Host "    copied untracked config: $_"
        }
    # Keep BehaveDiff's own run artifacts out of the clone's git status.
    Add-Content (Join-Path $clone '.git/info/exclude') "`n.behavediff/"

    foreach ($e in $Edit) {
        $parts = $e -split '::', 3
        if ($parts.Count -ne 3) { throw "Edit must be 'path::find::replace': $e" }
        $file = Join-Path $clone $parts[0]
        $text = Get-Content $file -Raw
        $count = ([regex]::Matches($text, [regex]::Escape($parts[1]))).Count
        if ($count -ne 1) { throw "Edit target must occur exactly once in $($parts[0]) (found $count): $($parts[1])" }
        Set-Content $file ($text.Replace($parts[1], $parts[2])) -NoNewline
        Write-Host "    edited $($parts[0])"
    }
    if ($Edit.Count -gt 0) { git -C $clone --no-pager diff --stat | Out-Host }

    $config = Join-Path $out 'behavediff.yml'
    Set-Content $config "inputs:`n  - kind: mstest`n    project: $($TestProject.Replace('\', '/'))`n"

    Step 'Running behavediff'
    $bdArgs = @('run', '--repo', $clone, '--config', $config, '--out', (Join-Path $out 'report.json'))
    if ($Intent) { $bdArgs += @('--intent', $Intent) }
    foreach ($x in $Expect) { $bdArgs += @('--expect', $x) }
    if ($NoSelfNoise) { $bdArgs += '--no-self-noise' }
    & dotnet (Join-Path $cliOut 'behavediff.dll') @bdArgs 2>&1 | Tee-Object -FilePath (Join-Path $out 'report.txt') | Out-Host
    $exit = $LASTEXITCODE

    # Keep the run artifacts (captures, TRX, logs) next to the report; the clone gets deleted.
    $runs = Join-Path $clone '.behavediff/runs'
    if (Test-Path $runs) { Copy-Item -Recurse $runs (Join-Path $out 'runs') }

    Step "behavediff exit code: $exit"
    Write-Host "  report: $(Join-Path $out 'report.txt')"
    Write-Host "  json:   $(Join-Path $out 'report.json')"
    exit $exit
}
finally {
    if (-not $KeepClone) { Remove-Item -Recurse -Force $clone -ErrorAction SilentlyContinue }
}
