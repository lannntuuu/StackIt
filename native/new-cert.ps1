<#
.SYNOPSIS
  Create or reuse the StackIt self-signed code-signing certificate (run on the dev machine)
.DESCRIPTION
  Creates a code-signing self-signed cert (idempotent: reuses an existing one),
  installs it into the current user's "Root" and "Trusted Publishers" stores, and
  exports the public key .cer for internal colleagues.
  The "Root" store is required so the signature chain is trusted and verification
  reports Valid on this machine. Publish with .\publish.ps1 after this.
#>
[CmdletBinding()]
param(
    [string] $Subject = 'CN=StackIt Self-Signed',
    [int]    $Years   = 5
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$cerPath = Join-Path $root 'StackIt-CodeSign.cer'

# 1. Reuse existing cert if present; otherwise create one
$cert = @(Get-ChildItem Cert:\CurrentUser\My) |
    Where-Object { $_.Subject -eq $Subject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Subject $Subject `
        -Type CodeSigningCert `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyUsage DigitalSignature `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears($Years) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
    Write-Host "Created cert: $($cert.Thumbprint)  ($Subject)"
} else {
    Write-Host "Reusing existing cert: $($cert.Thumbprint)  ($Subject)"
}

# Always (re)export the public key so colleagues can install it
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

# 2. Install into current user stores so the chain is trusted locally (no admin needed).
#    Root is required for Get-AuthenticodeSignature to report Valid; TrustedPublisher
#    is what SmartScreen shows as the known publisher.
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null

Write-Host "Installed into current user\Root + TrustedPublisher on this machine."
Write-Host ""
Write-Host "=== Internal colleague install (distribute StackIt-CodeSign.cer, run as Admin PowerShell) ==="
Write-Host "Import-Certificate -FilePath StackIt-CodeSign.cer -CertStoreLocation Cert:\LocalMachine\Root"
Write-Host "Import-Certificate -FilePath StackIt-CodeSign.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher"
Write-Host ""
Write-Host "Public cert exported: $cerPath"
