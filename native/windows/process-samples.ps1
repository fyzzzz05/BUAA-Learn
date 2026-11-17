[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$RootProcess,
    [ValidateRange(1, 480)][int]$DurationMinutes = 5,
    [ValidateRange(2, 60)][int]$IntervalSeconds = 10
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Get-Process -Id $RootProcess
$created = $root.StartTime
$timer = [Diagnostics.Stopwatch]::StartNew()
$measurements = [Collections.Generic.List[object]]::new()
$cores = [Environment]::ProcessorCount
$previous = @{}
$cpuDelta = 0.0
$lastSample = 0.0
while ($true) {
    $liveRoot = Get-Process -Id $RootProcess -ErrorAction SilentlyContinue
    if (-not $liveRoot -or $liveRoot.StartTime -ne $created) { throw 'The app exited before the sampling window ended.' }
    $processes = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($RootProcess)
    do {
        $count = $ids.Count
        foreach ($row in $processes) {
            if ($ids.Contains([int]$row.ParentProcessId)) { [void]$ids.Add([int]$row.ProcessId) }
        }
    } while ($ids.Count -ne $count)
    $elapsed = $timer.Elapsed.TotalSeconds
    $private = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Property IDProcess,WorkingSetPrivate |
        Where-Object { $ids.Contains([int]$_.IDProcess) } | Measure-Object -Property WorkingSetPrivate -Sum
    $current = @{}
    $handles = 0
    foreach ($item in (Get-Process -Id @($ids) -ErrorAction SilentlyContinue)) {
        try {
            $key = "$($item.Id):$($item.StartTime.Ticks)"
            $cpu = [double]$item.CPU
            $current[$key] = $cpu
            if ($previous.ContainsKey($key)) { $cpuDelta += [Math]::Max(0, $cpu - $previous[$key]) }
            elseif ($measurements.Count -gt 0) { $cpuDelta += $cpu }
            $handles += $item.HandleCount
        } catch [InvalidOperationException] { }
    }
    $previous = $current
    $measurements.Add(@{ elapsed_seconds = [Math]::Round($elapsed, 2); processes = $ids.Count;
        private_working_set_mib = [Math]::Round([double]$private.Sum / 1MB, 2); handles = $handles })
    if ($measurements.Count -eq 1) { $firstSample = $elapsed }
    $lastSample = $elapsed
    if ($elapsed -ge $DurationMinutes * 60) { break }
    Start-Sleep -Seconds $IntervalSeconds
}
[ordered]@{
    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    logical_processors = $cores
    root_process = $root.ProcessName
    average_observed_cpu_percent = [Math]::Round(100 * $cpuDelta / (($lastSample - $firstSample) * $cores), 3)
    scope = 'Root process and observed descendants. Processes that exit between samples may be missed.'
    samples = $measurements
} | ConvertTo-Json -Depth 4
