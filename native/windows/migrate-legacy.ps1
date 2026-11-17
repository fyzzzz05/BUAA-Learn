[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$LegacyDatabase,
    [Parameter(Mandatory)][string]$SchoolAccount,
    [string]$Executable = (Join-Path $PSScriptRoot 'BUAALearn.Probe.exe'),
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$LegacyDatabase = (Resolve-Path -LiteralPath $LegacyDatabase).Path
if (Get-Process -Name 'BUAA Learn','BUAALearn.Probe' -ErrorAction SilentlyContinue) {
    throw 'Close BUAA Learn (both versions) before running the migration.'
}
if ($Apply -and -not $PSCmdlet.ShouldProcess($LegacyDatabase, 'Back up and import this school account')) { return }
$mode = if ($Apply) { 'apply' } else { 'preview' }
$report = Join-Path ([IO.Path]::GetTempPath()) ("buaa-migration-" + [Guid]::NewGuid().ToString('N') + '.json')
$start = [Diagnostics.ProcessStartInfo]::new($Executable)
$start.UseShellExecute = $false
foreach ($arg in @('--legacy-migration', $LegacyDatabase, $SchoolAccount, $mode, $report)) { $start.ArgumentList.Add($arg) }
$process = [Diagnostics.Process]::Start($start)
try {
    $process.WaitForExit()
    if (-not (Test-Path -LiteralPath $report -PathType Leaf)) { throw "Migration helper exited without a report ($($process.ExitCode))." }
    Get-Content -LiteralPath $report -Raw -Encoding UTF8
    if ($process.ExitCode -ne 0) { throw "Migration did not apply changes (exit $($process.ExitCode))." }
} finally {
    $process.Dispose()
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
}
