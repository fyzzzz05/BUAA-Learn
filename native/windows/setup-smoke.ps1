[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Upgrade,
    [switch]$SkipAppSmoke
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path $env:LOCALAPPDATA 'Programs\BUAA Learn Native'
$data = Join-Path $env:LOCALAPPDATA 'BUAA Learn Native'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BUAALearnNative'
if (Test-Path -LiteralPath $root) { throw 'Use a clean Windows test account without BUAA Learn Native installed.' }
$createdData = -not (Test-Path -LiteralPath $data)
$sentinel = Join-Path $data ("installer-smoke-" + [Guid]::NewGuid().ToString('N') + '.txt')
function Run-Setup([string]$Path) {
    $installer = Start-Process -FilePath (Resolve-Path -LiteralPath $Path).Path -ArgumentList '/S' -Wait -PassThru
    if ($installer.ExitCode -ne 0) { throw "Installer exited with code $($installer.ExitCode)." }
}
function Current-Exe {
    $programs = [Environment]::GetFolderPath('Programs')
    if (-not $programs -and $SkipAppSmoke) {
        $target = (Get-ItemProperty -LiteralPath $key).AppExecutable
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Registered executable is missing.' }
        return $target
    }
    $link = Join-Path $programs 'BUAA Learn Native\BUAA Learn Native.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $target = $shell.CreateShortcut($link).TargetPath
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Start menu shortcut is missing or invalid.' }
    return $target
}
function Uninstall {
    $uninstaller = Join-Path $root 'Uninstall.exe'
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        if ($SkipAppSmoke) {
            # Guest-agent fixtures run in the redirected SYSTEM profile.
            $copy = Join-Path ([IO.Path]::GetTempPath()) ("buaa-uninstall-" + [Guid]::NewGuid().ToString('N') + '.exe')
            Copy-Item -LiteralPath $uninstaller -Destination $copy
            try { $process = Start-Process -FilePath $copy -ArgumentList "/S _?=$root" -Wait -PassThru }
            finally { Remove-Item -LiteralPath $copy }
        } else {
            $process = Start-Process -FilePath $uninstaller -ArgumentList '/S' -Wait -PassThru
        }
        if ($process.ExitCode -ne 0) { throw "Uninstaller exited with code $($process.ExitCode)." }
    }
}
try {
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Set-Content -LiteralPath $sentinel -Value 'synthetic user data'
    Run-Setup $Setup
    $first = Current-Exe
    if (-not (Test-Path -LiteralPath $key)) { throw 'Uninstall registry entry is missing.' }
    if (-not $SkipAppSmoke) { & (Join-Path $PSScriptRoot 'smoke.ps1') -Executable $first -Workspace | Out-Null }
    Run-Setup $Upgrade
    $second = Current-Exe
    if ($first -eq $second -or -not (Test-Path -LiteralPath $sentinel)) { throw 'Upgrade did not switch versions or keep user data.' }
    if (-not $SkipAppSmoke) { & (Join-Path $PSScriptRoot 'smoke.ps1') -Executable $second -Workspace | Out-Null }
    Uninstall
    if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $key) -or -not (Test-Path -LiteralPath $sentinel)) {
        throw 'Uninstall did not remove the app or preserve the data directory.'
    }
    @{ installed = $true; upgraded = $true; uninstalled = $true; data_preserved = $true; app_smoke = -not $SkipAppSmoke } | ConvertTo-Json
}
finally {
    Uninstall
    if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
    if ($createdData -and (Test-Path -LiteralPath $data) -and -not (Get-ChildItem -LiteralPath $data -Force)) {
        Remove-Item -LiteralPath $data
    }
}
