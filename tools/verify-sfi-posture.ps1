#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Verifies the SFI posture of a deployed BlinkMark environment and writes an attestation (T135).

.DESCRIPTION
    tools/sfi-gate.ps1 checks what the templates *say*. This checks what actually exists.

    The two are not the same claim. A template can be correct while a resource drifts, is
    patched by hand, or is created outside the pipeline. This script reads the live resources and
    fails if any of them accepts local authentication or is reachable from the public internet.

    Checks, matching the wording of T135:
      * storage       allowSharedKeyAccess is false on both accounts
      * cosmos        disableLocalAuth is true
      * redis         disableAccessKeyAuthentication is true
      * log analytics local auth is disabled
      * key vault     RBAC authorization mode, no access policies
      * every data service has publicNetworkAccess disabled

.PARAMETER ResourceGroup
    Resource group holding the environment.

.PARAMETER OutputPath
    Markdown attestation to write. Written whether the run passes or fails, because a failed
    attestation is the useful artifact.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [string] $SubscriptionId,
    [string] $OutputPath = 'docs/sfi-attestation.md'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required.'
}

if ($SubscriptionId) {
    az account set --subscription $SubscriptionId | Out-Null
}

$results = [System.Collections.Generic.List[pscustomobject]]::new()

function Add-Check {
    param(
        [string] $Resource,
        [string] $Control,
        [string] $Expected,
        [string] $Actual
    )
    $results.Add([pscustomobject]@{
            Resource = $Resource
            Control  = $Control
            Expected = $Expected
            Actual   = $Actual
            Pass     = ($Actual -eq $Expected)
        })
}

function Get-Json {
    param([string[]] $Arguments)
    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments -join ' ') failed: $output" }
    if ([string]::IsNullOrWhiteSpace($output)) { return $null }
    return ($output | ConvertFrom-Json)
}

Write-Host "Verifying SFI posture in resource group '$ResourceGroup'" -ForegroundColor Cyan

# --- Storage -----------------------------------------------------------------
$storageAccounts = Get-Json @('storage', 'account', 'list', '--resource-group', $ResourceGroup, '--output', 'json')
foreach ($account in $storageAccounts) {
    Add-Check -Resource $account.name -Control 'allowSharedKeyAccess' -Expected 'False' -Actual "$($account.allowSharedKeyAccess)"
    Add-Check -Resource $account.name -Control 'defaultToOAuthAuthentication' -Expected 'True' -Actual "$($account.defaultToOAuthAuthentication)"
    Add-Check -Resource $account.name -Control 'allowBlobPublicAccess' -Expected 'False' -Actual "$($account.allowBlobPublicAccess)"
    Add-Check -Resource $account.name -Control 'publicNetworkAccess' -Expected 'Disabled' -Actual "$($account.publicNetworkAccess)"
    Add-Check -Resource $account.name -Control 'minimumTlsVersion' -Expected 'TLS1_2' -Actual "$($account.minimumTlsVersion)"
    Add-Check -Resource $account.name -Control 'supportsHttpsTrafficOnly' -Expected 'True' -Actual "$($account.enableHttpsTrafficOnly)"
}

# --- Cosmos ------------------------------------------------------------------
$cosmosAccounts = Get-Json @('cosmosdb', 'list', '--resource-group', $ResourceGroup, '--output', 'json')
foreach ($account in $cosmosAccounts) {
    Add-Check -Resource $account.name -Control 'disableLocalAuth' -Expected 'True' -Actual "$($account.disableLocalAuth)"
    Add-Check -Resource $account.name -Control 'publicNetworkAccess' -Expected 'Disabled' -Actual "$($account.publicNetworkAccess)"
    Add-Check -Resource $account.name -Control 'minimalTlsVersion' -Expected 'Tls12' -Actual "$($account.minimalTlsVersion)"
}

# --- Redis -------------------------------------------------------------------
$redisCaches = Get-Json @('redis', 'list', '--resource-group', $ResourceGroup, '--output', 'json')
foreach ($cache in $redisCaches) {
    Add-Check -Resource $cache.name -Control 'disableAccessKeyAuthentication' -Expected 'True' -Actual "$($cache.disableAccessKeyAuthentication)"
    Add-Check -Resource $cache.name -Control 'enableNonSslPort' -Expected 'False' -Actual "$($cache.enableNonSslPort)"
    Add-Check -Resource $cache.name -Control 'publicNetworkAccess' -Expected 'Disabled' -Actual "$($cache.publicNetworkAccess)"
    Add-Check -Resource $cache.name -Control 'minimumTlsVersion' -Expected '1.2' -Actual "$($cache.minimumTlsVersion)"
}

# --- Log Analytics -----------------------------------------------------------
$workspaces = Get-Json @('monitor', 'log-analytics', 'workspace', 'list', '--resource-group', $ResourceGroup, '--output', 'json')
foreach ($workspace in $workspaces) {
    $disableLocalAuth = $null
    if ($workspace.PSObject.Properties.Name -contains 'features' -and $null -ne $workspace.features) {
        $disableLocalAuth = $workspace.features.disableLocalAuth
    }
    Add-Check -Resource $workspace.name -Control 'features.disableLocalAuth' -Expected 'True' -Actual "$disableLocalAuth"
    Add-Check -Resource $workspace.name -Control 'publicNetworkAccessForIngestion' -Expected 'Disabled' -Actual "$($workspace.publicNetworkAccessForIngestion)"
}

# --- Key Vault ---------------------------------------------------------------
$vaults = Get-Json @('keyvault', 'list', '--resource-group', $ResourceGroup, '--output', 'json')
foreach ($vaultSummary in $vaults) {
    $vault = Get-Json @('keyvault', 'show', '--name', $vaultSummary.name, '--resource-group', $ResourceGroup, '--output', 'json')
    Add-Check -Resource $vault.name -Control 'enableRbacAuthorization' -Expected 'True' -Actual "$($vault.properties.enableRbacAuthorization)"
    $policyCount = 0
    if ($null -ne $vault.properties.accessPolicies) { $policyCount = @($vault.properties.accessPolicies).Count }
    Add-Check -Resource $vault.name -Control 'accessPolicies count' -Expected '0' -Actual "$policyCount"
    Add-Check -Resource $vault.name -Control 'publicNetworkAccess' -Expected 'Disabled' -Actual "$($vault.properties.publicNetworkAccess)"
}

# --- Attestation -------------------------------------------------------------
$failed = @($results | Where-Object { -not $_.Pass })
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# SFI posture attestation')
$lines.Add('')
$lines.Add("**Resource group**: ``$ResourceGroup``  ")
$lines.Add("**Verified**: $timestamp  ")
$lines.Add("**Result**: $(if ($failed.Count -eq 0) { 'PASS' } else { "FAIL — $($failed.Count) finding(s)" })")
$lines.Add('')
$lines.Add('This file is generated by `tools/verify-sfi-posture.ps1` against live resources. It')
$lines.Add('records what is deployed, not what the templates claim. Do not edit it by hand.')
$lines.Add('')
$lines.Add('| Resource | Control | Expected | Actual | Result |')
$lines.Add('|---|---|---|---|---|')
foreach ($check in $results) {
    $mark = if ($check.Pass) { 'PASS' } else { '**FAIL**' }
    $lines.Add("| ``$($check.Resource)`` | $($check.Control) | $($check.Expected) | $($check.Actual) | $mark |")
}
$lines.Add('')
$lines.Add('## What is not asserted here')
$lines.Add('')
$lines.Add('Table Storage has no platform-enforced write-once guarantee (research.md R8). The audit')
$lines.Add('trail is append-only by application contract, backed by a repository interface that')
$lines.Add('exposes only `AppendAsync`, a resource lock on the account, and a test asserting no')
$lines.Add('delete or merge path exists. That is a real control, but it is not the platform control')
$lines.Add('the word "immutable" usually implies, and this attestation does not claim otherwise.')

$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}
Set-Content -LiteralPath $OutputPath -Value $lines -Encoding utf8

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host "SFI posture verification FAILED with $($failed.Count) finding(s):" -ForegroundColor Red
    foreach ($check in $failed) {
        Write-Host "  $($check.Resource): $($check.Control) expected $($check.Expected), found '$($check.Actual)'" -ForegroundColor Red
    }
    Write-Host "Attestation written to $OutputPath" -ForegroundColor Yellow
    exit 1
}

Write-Host "SFI posture verified. Attestation written to $OutputPath" -ForegroundColor Green
exit 0
