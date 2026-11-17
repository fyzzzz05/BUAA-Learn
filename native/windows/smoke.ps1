[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable, [switch]$Workspace, [switch]$Seeded)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path $Executable).Path
$directory = Join-Path ([IO.Path]::GetTempPath()) ("buaa-native-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$report = Join-Path $directory 'startup.json'
$flag = if ($Seeded) { '--workspace-seeded-smoke-report' } elseif ($Workspace) { '--workspace-smoke-report' } else { '--smoke-report' }
try {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $executablePath -ArgumentList @($flag, ('"' + $report + '"')) -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Native startup timed out.' }
    $timer.Stop()
    if (-not (Test-Path $report)) { throw 'Application did not produce a startup report.' }
    $result = Get-Content $report -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or -not $result.ok) { throw 'Native ABI or WebView2 startup failed.' }
    if (($Workspace -or $Seeded) -and -not $result.workspace) { throw 'React workspace did not finish loading.' }
    if ($Seeded -and (-not $result.seeded -or $result.ready_elapsed_ms -le 0)) { throw 'Seeded course list did not load.' }
    $result | Add-Member -NotePropertyName smoke_elapsed_ms -NotePropertyValue $timer.ElapsedMilliseconds
    $result | ConvertTo-Json
} finally { Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue }
