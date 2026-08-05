<#
.SYNOPSIS
Publishes GlassCoder run transcripts and metrics into the repository.

.DESCRIPTION
Copies the live log files from the per-user data root (%LOCALAPPDATA%\GlassCoder, or
GLASSCODER_DATA_DIR) into the committed telemetry/ folder, prunes published transcripts
older than -RetainDays from the working tree, then commits and pushes. The point is remote
analysis: a session without access to this machine - Claude Code on claude.ai reading the
repo over GitHub - can then open telemetry/transcripts/ directly.

Run it before switching to a remote session. Windows PowerShell 5.1 compatible.

.PARAMETER RetainDays
Days of transcripts to keep in the working tree (git history keeps everything). Default 14.

.PARAMETER NoGit
Copy and prune only; skip the commit and push.
#>
[CmdletBinding()]
param(
    [int]$RetainDays = 14,
    [switch]$NoGit
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

$dataRoot = $env:GLASSCODER_DATA_DIR
if ([string]::IsNullOrWhiteSpace($dataRoot)) { $dataRoot = Join-Path $env:LOCALAPPDATA 'GlassCoder' }

$logSource = Join-Path $dataRoot 'logs'
$metricsSource = Join-Path $dataRoot 'metrics\metrics.jsonl'
$transcriptDest = Join-Path $repoRoot 'telemetry\transcripts'
$metricsDest = Join-Path $repoRoot 'telemetry\metrics'

if (-not (Test-Path $logSource)) {
    Write-Host "No live logs at $logSource - nothing to publish."
    exit 0
}

New-Item -ItemType Directory -Force $transcriptDest | Out-Null

# Serilog keeps today's file open, but with read sharing - a copy is a valid snapshot
# (at worst the final line is torn; every earlier line is complete JSON).
$copied = 0
foreach ($file in Get-ChildItem $logSource -File | Where-Object { $_.Extension -in '.jsonl', '.log' }) {
    Copy-Item $file.FullName (Join-Path $transcriptDest $file.Name) -Force
    $copied++
}

if (Test-Path $metricsSource) {
    New-Item -ItemType Directory -Force $metricsDest | Out-Null
    Copy-Item $metricsSource (Join-Path $metricsDest 'metrics.jsonl') -Force
}

$cutoff = (Get-Date).AddDays(-$RetainDays)
$pruned = 0
foreach ($file in Get-ChildItem $transcriptDest -File | Where-Object { $_.LastWriteTime -lt $cutoff }) {
    Remove-Item $file.FullName -Force
    $pruned++
}

Write-Host "Copied $copied file(s) from $logSource; pruned $pruned older than $RetainDays days."

if ($NoGit) { exit 0 }

Push-Location $repoRoot
try {
    git add telemetry
    if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

    git diff --cached --quiet -- telemetry
    if ($LASTEXITCODE -eq 0) {
        Write-Host 'No transcript changes to publish.'
    }
    else {
        $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
        git commit -m "Publish run transcripts through $stamp"
        if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

        git push
        if ($LASTEXITCODE -ne 0) { throw 'git push failed.' }
    }
}
finally {
    Pop-Location
}
