# Analyzes HiveMotion activation checkpoints from verbose logs.
# Parses "activation <name> +<ms>ms" records, groups them into activations
# (one per hotkey-received-ui), and reports per-stage and per-interval
# distributions plus timelines for the slowest activations.
#
# Usage:
#   .\tools\analyze-activation-logs.ps1                      # today's logs
#   .\tools\analyze-activation-logs.ps1 -From 2026-09-11 -To 2026-09-12
#   .\tools\analyze-activation-logs.ps1 -TopN 10
#   .\tools\analyze-activation-logs.ps1 -SelfTest            # built-in fixture check
[CmdletBinding()]
param(
    [string]$LogDir = "$env:LOCALAPPDATA\HiveMotion\Logs",
    [datetime]$From,
    [datetime]$To,
    [string]$Filter = 'hivemotion-*.log',
    [int]$TopN = 5,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# Matches lines like: 0912 12:03:34.097 I [DEFAULT] activation keyboard-ready +40.6ms thread=1
# Names are lowercase-hyphenated; "first-wpf-render (not hardware presentation)" carries a parenthetical.
$script:CheckpointRegex = '^(?<mmdd>\d{4})\s+(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\s+\S+\s+\[(?<channel>\w+)\]\s+activation\s+(?<name>[\w-]+)(?:\s+\([^)]*\))?\s+\+(?<ms>[\d.]+)ms'

function Get-Activations([string[]]$Lines) {
    $activations = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($line in $Lines) {
        $m = [regex]::Match($line, $script:CheckpointRegex)
        if (-not $m.Success) { continue }
        if ($m.Groups['name'].Value -eq 'hotkey-received-ui' -or $null -eq $current) {
            $current = [ordered]@{ Start = $m.Groups['time'].Value; Checkpoints = (New-Object System.Collections.Generic.List[object]) }
            $activations.Add($current)
        }
        $current.Checkpoints.Add([pscustomobject]@{
            Name = $m.Groups['name'].Value
            Ms   = [double]$m.Groups['ms'].Value
        })
    }
    return $activations
}

function Get-Stats([double[]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = $Values | Sort-Object
    $n = $sorted.Count
    $median = if ($n % 2 -eq 1) { $sorted[[int]($n / 2)] } else { ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2 }
    return [pscustomobject]@{
        Count   = $n
        Min     = [math]::Round($sorted[0], 1)
        Median  = [math]::Round($median, 1)
        P95     = [math]::Round($sorted[[int](0.95 * ($n - 1))], 1)
        Max     = [math]::Round($sorted[$n - 1], 1)
    }
}

function Write-StageTable($Title, $Rows) {
    Write-Host "`n=== $Title ==="
    $rows = $Rows | Sort-Object Median -Descending
    Write-Host ('{0,-44} {1,6} {2,8} {3,8} {4,8} {5,8}' -f 'stage', 'count', 'min', 'median', 'p95', 'max')
    foreach ($r in $rows) {
        Write-Host ('{0,-44} {1,6} {2,8} {3,8} {4,8} {5,8}' -f $r.Name, $r.Count, $r.Min, $r.Median, $r.P95, $r.Max)
    }
}

function Invoke-Analysis([string[]]$Lines, [int]$Slowest) {
    $activations = Get-Activations $Lines
    Write-Host "Parsed $($activations.Count) activation(s)."

    # Per-stage cumulative distributions.
    $stageBuckets = @{}
    foreach ($a in $activations) {
        foreach ($c in $a.Checkpoints) {
            if (-not $stageBuckets.ContainsKey($c.Name)) { $stageBuckets[$c.Name] = New-Object System.Collections.Generic.List[double] }
            $stageBuckets[$c.Name].Add([double]$c.Ms)
        }
    }
    $stageRows = foreach ($name in $stageBuckets.Keys) {
        $stats = Get-Stats ([double[]]$stageBuckets[$name].ToArray())
        if ($stats) { $stats | Add-Member -NotePropertyName Name -NotePropertyValue $name -PassThru }
    }
    Write-StageTable 'Stage cumulative times (ms since hotkey)' $stageRows

    # Per-interval distributions between consecutive checkpoints.
    $intervalBuckets = @{}
    foreach ($a in $activations) {
        for ($i = 1; $i -lt $a.Checkpoints.Count; $i++) {
            $name = "$($a.Checkpoints[$i - 1].Name) -> $($a.Checkpoints[$i].Name)"
            $delta = $a.Checkpoints[$i].Ms - $a.Checkpoints[$i - 1].Ms
            if (-not $intervalBuckets.ContainsKey($name)) { $intervalBuckets[$name] = New-Object System.Collections.Generic.List[double] }
            $intervalBuckets[$name].Add([double][math]::Round($delta, 1))
        }
    }
    $intervalRows = foreach ($name in $intervalBuckets.Keys) {
        $stats = Get-Stats ([double[]]$intervalBuckets[$name].ToArray())
        if ($stats) { $stats | Add-Member -NotePropertyName Name -NotePropertyValue $name -PassThru }
    }
    Write-StageTable 'Intervals between consecutive checkpoints (ms)' $intervalRows

    # Slowest activation timelines.
    $ranked = $activations | Sort-Object { $_.Checkpoints[$_.Checkpoints.Count - 1].Ms } -Descending | Select-Object -First $Slowest
    $rank = 0
    foreach ($a in $ranked) {
        $rank++
        Write-Host "`n--- Slowest #$rank (started $($a.Start), $($a.Checkpoints.Count) checkpoints) ---"
        $previous = $null
        foreach ($c in $a.Checkpoints) {
            $delta = if ($null -eq $previous) { $c.Ms } else { $c.Ms - $previous.Ms }
            Write-Host ("  {0,8:n1} ms  +{1,7:n1} ms  {2}" -f $c.Ms, $delta, $c.Name)
            $previous = $c
        }
    }
    return $activations.Count
}

if ($SelfTest) {
    $fixture = Join-Path $PSScriptRoot 'sample-activation.log.txt'
    $lines = Get-Content $fixture
    $activations = Get-Activations $lines
    if ($activations.Count -ne 2) { throw "SelfTest: expected 2 activations, got $($activations.Count)" }
    $first = $activations[0].Checkpoints
    if ($first[0].Name -ne 'hotkey-received-ui' -or [math]::Abs($first[0].Ms - 0.5) -gt 0.01) { throw 'SelfTest: first checkpoint mismatch' }
    $ready = $first | Where-Object Name -eq 'keyboard-ready'
    if (-not $ready -or [math]::Abs($ready.Ms - 40.6) -gt 0.01) { throw 'SelfTest: keyboard-ready checkpoint missing or wrong' }
    Write-Host "SelfTest passed: 2 activations, $($first.Count) + $($activations[1].Checkpoints.Count) checkpoints."
    Invoke-Analysis $Lines 1 | Out-Null
    return
}

$files = Get-ChildItem $LogDir -Filter $Filter | Sort-Object Name
if ($From) { $files = $files | Where-Object { $_.BaseName -match 'hivemotion-(\d{4}-\d{2}-\d{2})' -and [datetime]$Matches[1] -ge $From.Date } }
if ($To)   { $files = $files | Where-Object { $_.BaseName -match 'hivemotion-(\d{4}-\d{2}-\d{2})' -and [datetime]$Matches[1] -le $To.Date } }
if (-not $files) { Write-Host "No log files matched in $LogDir."; return }
Write-Host "Reading $($files.Count) file(s) from $LogDir ..."
$lines = foreach ($f in $files) { Get-Content $f.FullName }
Invoke-Analysis $Lines $TopN | Out-Null
