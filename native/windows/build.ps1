[CmdletBinding()]
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'WinUI build must run on Windows using PowerShell 7.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$core = Join-Path $root 'native/core/Cargo.toml'
$target = 'x86_64-pc-windows-msvc'
function Invoke-Checked([scriptblock]$Action) {
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}
function Copy-NuGetNotices([string]$Output) {
    $assetsPath = Join-Path $PSScriptRoot 'BUAALearn.App/obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    $deps = Get-Content -LiteralPath (Join-Path $Output 'BUAALearn.Probe.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $packages = @($assets.libraries.Keys) + @($deps.libraries.Keys | Where-Object { $_ -like 'runtimepack.*' } |
        ForEach-Object { $_.Substring('runtimepack.'.Length) })
    $destination = Join-Path $Output 'licenses/nuget'
    foreach ($package in ($packages | Sort-Object -Unique)) {
        if ($package -notlike 'Microsoft.*/*') { continue }
        foreach ($folder in $assets.packageFolders.Keys) {
            $directory = Join-Path $folder $package.ToLowerInvariant()
            if (-not (Test-Path -LiteralPath $directory -PathType Container)) { continue }
            $notices = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '^(license|notice|third.party.notices|copying)' })
            if ($notices.Count -gt 0) {
                $target = Join-Path $destination ($package.Replace('/','-'))
                New-Item -Path $target -ItemType Directory -Force | Out-Null
                foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $target }
            }
            break
        }
    }
}
Push-Location $PSScriptRoot
try {
    Push-Location (Join-Path $root 'frontend')
    try {
        Invoke-Checked { npm ci }
        Invoke-Checked { npm run typecheck }
        Invoke-Checked { npm run build:windows }
    } finally { Pop-Location }
    Invoke-Checked { cargo test --locked --manifest-path $core --target $target }
    Invoke-Checked { cargo build --release --locked --manifest-path $core --target $target }
    Invoke-Checked { dotnet restore BUAALearn.Tests/BUAALearn.Tests.csproj --locked-mode }
    Invoke-Checked { dotnet run --project BUAALearn.Tests -c Release --no-restore }
    Invoke-Checked { dotnet restore BUAALearn.App/BUAALearn.App.csproj -p:Platform=x64 --locked-mode }
    Invoke-Checked { dotnet build BUAALearn.App/BUAALearn.App.csproj -c Release -p:Platform=x64 --no-restore }
    if ($Publish) {
        Invoke-Checked { node (Join-Path $root 'frontend/collect-native-licenses.mjs') }
        $output = Join-Path $root 'dist/native/windows/probe'
        if (Test-Path $output) { Remove-Item $output -Recurse -Force }
        Invoke-Checked { dotnet publish BUAALearn.App/BUAALearn.App.csproj -c Release -p:Platform=x64 --no-restore -o $output }
        Copy-Item (Join-Path $root 'LICENSE') $output
        Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $output
        Copy-Item (Join-Path $root 'dist/native/windows/licenses') (Join-Path $output 'licenses') -Recurse
        Copy-NuGetNotices $output
        Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $output 'WINDOWS-PROBE.md')
        $package = Join-Path $root 'dist/native/windows/BUAA-Learn-Windows-Probe-x64.zip'
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath $package -Force
        $size = (Get-Item $package).Length
        @{ artifact = 'BUAA-Learn-Windows-Probe-x64.zip'; bytes = $size; school_verified = $false } |
            ConvertTo-Json | Set-Content (Join-Path $root 'dist/native/windows/package-metrics.json') -Encoding utf8
        Write-Host "Probe archive: $package ($size bytes). This is not an installer."
    }
} finally { Pop-Location }
