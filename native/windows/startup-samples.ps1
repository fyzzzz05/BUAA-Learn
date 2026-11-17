[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [ValidateRange(1, 100)][int]$Samples = 20
)
$ErrorActionPreference = 'Stop'
$values = @(for ($i = 0; $i -lt $Samples; $i++) {
    $report = & (Join-Path $PSScriptRoot 'smoke.ps1') -Executable $Executable -Seeded | ConvertFrom-Json
    [double]$report.ready_elapsed_ms
})
$sorted = @($values | Sort-Object)
function Percentile([double]$Quantile) {
    return $sorted[[Math]::Ceiling($sorted.Count * $Quantile) - 1]
}
$os = Get-CimInstance Win32_OperatingSystem
[ordered]@{
    scenario = 'Process start to 100 synthetic offline course cards in the WebView2 workspace'
    os = $os.Caption
    build = $os.BuildNumber
    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    samples_ms = $values
    p50_ms = (Percentile 0.50)
    p95_ms = (Percentile 0.95)
} | ConvertTo-Json
