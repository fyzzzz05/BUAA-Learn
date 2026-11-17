[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version = '0.1.0-preview',
    [string]$MakeNsis = 'makensis.exe',
    [string]$SignTool = 'signtool.exe',
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [string]$TimestampUrl
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$out = Join-Path $root 'dist/native/windows'
$payload = Join-Path $out 'probe'
if ($CertificateThumbprint -and $TimestampUrl -notmatch '^https://') { throw 'Signing requires an HTTPS timestamp URL.' }
function Invoke-Checked([scriptblock]$Action) {
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Packaging command failed ($LASTEXITCODE)." }
}
function Sign([string]$Path) {
    Invoke-Checked { & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path }
    Invoke-Checked { & $SignTool verify /pa $Path }
}
function Assert-X64([string]$Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Invalid Windows executable.' }
        $reader.BaseStream.Position = 0x3c
        $reader.BaseStream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Expected an x64 Windows executable.' }
    } finally { $reader.Dispose() }
}
$required = @('BUAALearn.Probe.exe', 'buaa_core.dll', 'Workspace/index.html', 'Workspace/app.js', 'Workspace/app.css',
    'LICENSE', 'THIRD_PARTY_NOTICES.md', 'licenses/manifest.json', 'migrate-legacy.ps1')
foreach ($entry in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $entry) -PathType Leaf)) { throw "Missing payload entry: $entry" }
}
$compiler = (Get-Command -Name $MakeNsis -CommandType Application).Source
$nsisLicense = Join-Path (Split-Path -Parent $compiler) 'COPYING'
if (-not (Test-Path -LiteralPath $nsisLicense -PathType Leaf)) { throw 'NSIS COPYING notice not found beside makensis.exe.' }
Copy-Item -LiteralPath $nsisLicense -Destination (Join-Path $payload 'licenses/NSIS-COPYING.txt') -Force
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -Force -File)
foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $file.Name -match '^(?:\.env(?:\.|$)|.*\.(?:sqlite3?|db|pfx|pem|key|log|dmp)(?:-wal|-shm)?$)' -or
        $file.FullName -match '[\\/](?:data|exports|backups|node_modules|\.git)[\\/]') {
        throw "Disallowed installer payload entry: $($file.Name)"
    }
}
$exe = Join-Path $payload 'BUAALearn.Probe.exe'
$dll = Join-Path $payload 'buaa_core.dll'
Assert-X64 $exe; Assert-X64 $dll
if ($CertificateThumbprint) { Sign $exe; Sign $dll }
$payloadManifest = ($files | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),
        ([IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\','/'))
}) -join "`n"
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($payloadManifest))).ToLowerInvariant()
$setup = Join-Path $out "BUAA-Learn-Native-$Version-win-x64-setup.exe"
Invoke-Checked { & $compiler "/DAPP_VERSION=$Version" "/DBUILD_ID=$($hash.Substring(0,12))" "/DPAYLOAD_DIR=$payload" "/DSETUP_OUT=$setup" (Join-Path $PSScriptRoot 'setup.nsi') }
if ($CertificateThumbprint) { Sign $setup }
$checksum = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $([IO.Path]::GetFileName($setup))" | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8
@{ signed = [bool]$CertificateThumbprint; bytes = (Get-Item -LiteralPath $setup).Length; sha256 = $checksum; payload = $hash } |
    ConvertTo-Json | Set-Content (Join-Path $out 'installer-metrics.json') -Encoding utf8
