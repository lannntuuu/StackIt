<#
.SYNOPSIS
  dotnet publish + sign StackIt.App.exe with the self-signed cert
.DESCRIPTION
  Publishes the WPF app to native/dist and signs StackIt.App.exe with the
  self-signed cert from this machine's Trusted Publishers store, so Windows
  SmartScreen no longer shows the "unrecognized app" warning.
  Run .\new-cert.ps1 once before first use to create and install the cert.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [switch] $SelfContained,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$proj   = Join-Path $root 'StackIt.App\StackIt.App.csproj'
$outDir = Join-Path $root 'dist'
$certSubject = 'CN=StackIt Self-Signed'

# 1. publish (output to native/dist, matching README self-test commands)
$pubArgs = @('publish', $proj, '-c', $Configuration, '-r', $Runtime, '-o', $outDir)
if ($SelfContained) { $pubArgs += '--self-contained' } else { $pubArgs += '--no-self-contained' }
if ($NoRestore) { $pubArgs += '--no-restore' }
Write-Host "==> dotnet $($pubArgs -join ' ')"
& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# 2. find cert
$cert = @(Get-ChildItem Cert:\CurrentUser\My) |
    Where-Object { $_.Subject -eq $certSubject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $cert) {
    throw "Self-signed cert [$certSubject] not found. Run .\new-cert.ps1 first."
}

# 3. sign
$exe = Join-Path $outDir 'StackIt.App.exe'
if (-not (Test-Path $exe)) { throw "Published exe not found: $exe" }

Write-Host "==> Signing with cert: $($cert.Thumbprint)"
Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -TimestampServer 'http://timestamp.digicert.com'

$status = Get-AuthenticodeSignature $exe
if ($status.Status -ne 'Valid') { throw "Signature status: $($status.StatusMessage)" }
Write-Host "Signed: $exe (Status=$($status.Status))"
