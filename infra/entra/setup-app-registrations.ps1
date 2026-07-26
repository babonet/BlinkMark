#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Registers the BlinkMark API and SPA applications in Microsoft Entra ID (T013).

.DESCRIPTION
    Two single-tenant registrations, no client secrets, and no certificates.

    The interesting part is the API registration's credential. BlinkMark's agent access uses the
    On-Behalf-Of flow (research.md R5), and OBO normally requires the API to authenticate to
    Entra with a client secret or certificate — which would be exactly the retrievable secret
    Principle VII forbids. A **federated identity credential backed by the user-assigned managed
    identity** resolves the conflict: the API presents a token issued to its own managed identity
    as the client assertion, so the OBO exchange succeeds with nothing to store, rotate, or leak.

    Without this, SFI compliance and agent access would be in direct opposition and one of them
    would have to be dropped.

.PARAMETER TenantId
    Entra tenant that owns both registrations. Single-tenant by construction (Principle I).

.PARAMETER ManagedIdentityResourceId
    Resource id of the user-assigned managed identity created by infra/modules/identity.bicep.

.PARAMETER ServiceTreeId
    Service Tree id of the owning service.

    Required. Every application registered in the corporate tenant has to be traceable to a
    registered service, because an application nobody owns is an application nobody patches,
    nobody is paged for, and nobody decommissions. It is written to the application's tags and
    notes so that ownership survives the departure of whoever ran this script.

.PARAMETER AppOrigin
    Public origin of the SPA, for example https://app.blinkmark.example.com.

.PARAMETER WhatIf
    Print the actions without performing them.

.EXAMPLE
    ./setup-app-registrations.ps1 `
        -TenantId 72f988bf-86f1-41af-91ab-2d7cd011db47 `
        -ManagedIdentityResourceId /subscriptions/.../userAssignedIdentities/id-blinkmark-dev `
        -ServiceTreeId 00000000-0000-0000-0000-000000000000 `
        -AppOrigin https://app.blinkmark.example.com
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $ManagedIdentityResourceId,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $ServiceTreeId,
    [Parameter(Mandatory)] [string] $AppOrigin,
    [string] $ApiDisplayName = 'BlinkMark API',
    [string] $SpaDisplayName = 'BlinkMark',
    [string] $OutputPath = "$PSScriptRoot/app-registrations.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Which tenant are we registering in, and does the caller understand the consequences?
#
# The Corp tenant (MSIT/CSEO) is a production environment governed by DSR. Registering here
# commits the project to the governed path: Service Tree inventory, and App Admin Consent through
# SPACE for any permission needing admin consent — which itself is gated on SDL, Privacy, and RAI
# reviews. That is right for a service Microsoft employees will actually use, and heavy for a
# hackathon.
#
# The app registration decision guide is explicit that Corp is NOT recommended for non-production
# applications, and that dev/test work belongs in TME or TestTorus, where restrictions are looser
# and no SAW is required. There is no hackathon-specific consent exemption; choosing the right
# tenant is the exemption.
#
# The trade is real either way, so this warns rather than blocks:
#   - TME/TestTorus has no real employee accounts, so nobody can sign in with their work
#     identity, so nobody can sign in as a colleague.
#   - Corp gives you both, at the cost of the review process.
# -----------------------------------------------------------------------------

$corpTenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47'

if ($TenantId -eq $corpTenantId) {
    Write-Host ''
    Write-Host 'Registering in the Corp (MSIT) tenant — the governed path.' -ForegroundColor Yellow
    Write-Host '  * Every app here must be inventoried in Service Tree (enforced).' -ForegroundColor Yellow
    Write-Host '  * Client secrets and pinned certificates are refused by DSR policy, which is why' -ForegroundColor Yellow
    Write-Host '    this script creates neither.' -ForegroundColor Yellow
    Write-Host '  * Admin consent is not self-service: it goes through SPACE (see the end of this run).' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  If this is a hackathon, prototype, or demo, consider TME or TestTorus instead —' -ForegroundColor Yellow
    Write-Host '  Corp is explicitly not recommended for non-production apps. You would give up real' -ForegroundColor Yellow
    Write-Host '  employee sign-in and real mailbox delivery, and skip the review gauntlet entirely.' -ForegroundColor Yellow
    Write-Host ''
}
else {
    Write-Host ''
    Write-Host "Registering in tenant $TenantId (not Corp)." -ForegroundColor Cyan
    Write-Host '  Test tenants have no real employee accounts, so plan for test users rather than' -ForegroundColor Cyan
    Write-Host '  colleagues signing in with their real work identities.' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  TME/TestTorus removes the SPACE consent path, and removes nothing else:' -ForegroundColor Cyan
    Write-Host '    * TME is in SFI scope. The same S360 KPIs apply, including MISE v2 token' -ForegroundColor Cyan
    Write-Host '      validation — a test tenant is not a compliance holiday.' -ForegroundColor Cyan
    Write-Host '    * No customer data, ever. BlinkMark stores whatever a user uploads, so demo' -ForegroundColor Cyan
    Write-Host '      content must be synthetic.' -ForegroundColor Cyan
    Write-Host '    * Not for automated testing with user tokens — accounts are Corp-guested and' -ForegroundColor Cyan
    Write-Host '      carry the same MFA requirements. Use an Ephemeral or Test Automation' -ForegroundColor Cyan
    Write-Host '      Auxiliary tenant for signed-in end-to-end suites.' -ForegroundColor Cyan
    Write-Host ''
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required. Install it, then run: az login --tenant <tenantId>'
}

Write-Host "Tenant: $TenantId" -ForegroundColor Cyan

# -----------------------------------------------------------------------------
# Helpers
# -----------------------------------------------------------------------------

function Invoke-Graph {
    param(
        [Parameter(Mandatory)] [ValidateSet('GET', 'POST', 'PATCH', 'DELETE')] [string] $Method,
        [Parameter(Mandatory)] [string] $Uri,
        [object] $Body
    )

    $arguments = @('rest', '--method', $Method.ToLower(), '--uri', $Uri, '--headers', 'Content-Type=application/json')
    if ($null -ne $Body) {
        $json = ($Body | ConvertTo-Json -Depth 20 -Compress)
        $temporaryFile = New-TemporaryFile
        Set-Content -LiteralPath $temporaryFile -Value $json -Encoding utf8
        $arguments += @('--body', "@$temporaryFile")
    }

    $result = & az @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Microsoft Graph call failed: $result"
    }
    if ([string]::IsNullOrWhiteSpace($result)) { return $null }
    return ($result | ConvertFrom-Json)
}

function Get-ApplicationByDisplayName {
    param([string] $DisplayName)
    $encoded = [System.Uri]::EscapeDataString("displayName eq '$DisplayName'")
    $response = Invoke-Graph -Method GET -Uri "https://graph.microsoft.com/v1.0/applications?`$filter=$encoded"
    if ($response.value.Count -gt 0) { return $response.value[0] }
    return $null
}

# Stable scope identifiers. Regenerating them on every run would invalidate existing consent.
$filesScopeId = '4f2c1ad0-6b17-4a5f-8b41-1c9c7b2a5d31'
$commentsScopeId = 'b7c4e9d2-32a8-4f6d-9d0e-5a1f8c3b4e77'

# -----------------------------------------------------------------------------
# 1. API registration
# -----------------------------------------------------------------------------

Write-Host "`nAPI registration: $ApiDisplayName" -ForegroundColor Cyan

$apiBody = @{
    displayName            = $ApiDisplayName
    # Ownership metadata, carried on the application object itself so it survives whoever ran
    # this script moving teams.
    tags                   = @("ServiceTreeId:$ServiceTreeId", 'BlinkMark')
    notes                  = "BlinkMark API. Service Tree id $ServiceTreeId. Single-tenant, no client secret or certificate; the On-Behalf-Of exchange uses a federated identity credential backed by the user-assigned managed identity."
    # Single tenant. Principle I is not "authenticated" — it is "authenticated *and* a member of
    # the owning organization". A multi-tenant registration would make that a code check that
    # someone can forget rather than a platform property.
    signInAudience         = 'AzureADMyOrg'
    api                    = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes      = @(
            @{
                id                      = $filesScopeId
                value                   = 'Files.ReadWrite'
                type                    = 'User'
                isEnabled               = $true
                adminConsentDisplayName = 'Read and write BlinkMark files'
                adminConsentDescription = 'Allows the application to upload, read, extend, and delete files on behalf of the signed-in user.'
                userConsentDisplayName  = 'Read and write your BlinkMark files'
                userConsentDescription  = 'Allows the application to upload, read, extend, and delete your files on your behalf.'
            },
            @{
                id                      = $commentsScopeId
                value                   = 'Comments.ReadWrite'
                type                    = 'User'
                isEnabled               = $true
                adminConsentDisplayName = 'Read and write BlinkMark comments'
                adminConsentDescription = 'Allows the application to read and create review comments on behalf of the signed-in user.'
                userConsentDisplayName  = 'Read and write your BlinkMark comments'
                userConsentDescription  = 'Allows the application to read and create review comments on your behalf.'
            }
        )
    }
    # No passwordCredentials. No keyCredentials. Deliberately.
    requiredResourceAccess = @(
        @{
            # Microsoft Graph
            resourceAppId  = '00000003-0000-0000-c000-000000000000'
            resourceAccess = @(
                # User.Read, delegated. Display name resolution for presence and attribution.
                #
                # This is the ONLY permission BlinkMark requests, and it needs no admin consent -
                # every user consents to it for themselves. Notifications are delivered in-app
                # (research.md R9), so there is no Mail.Send grant, no application permission of
                # any kind, and nothing for a tenant administrator to approve.
                @{ id = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'; type = 'Scope' }
            )
        }
    )
}

$apiApp = Get-ApplicationByDisplayName -DisplayName $ApiDisplayName
if ($null -eq $apiApp) {
    if ($PSCmdlet.ShouldProcess($ApiDisplayName, 'Create API application registration')) {
        $apiApp = Invoke-Graph -Method POST -Uri 'https://graph.microsoft.com/v1.0/applications' -Body $apiBody
        Write-Host "  created appId $($apiApp.appId)" -ForegroundColor Green
    }
}
else {
    if ($PSCmdlet.ShouldProcess($ApiDisplayName, 'Update API application registration')) {
        Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)" -Body $apiBody | Out-Null
        Write-Host "  updated appId $($apiApp.appId)" -ForegroundColor Green
    }
}

if ($null -ne $apiApp -and [string]::IsNullOrEmpty($apiApp.identifierUris)) {
    if ($PSCmdlet.ShouldProcess($ApiDisplayName, 'Set identifier URI')) {
        Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)" -Body @{
            identifierUris = @("api://$($apiApp.appId)")
        } | Out-Null
    }
}

# -----------------------------------------------------------------------------
# 2. Federated identity credential — the reason there is no client secret
# -----------------------------------------------------------------------------

Write-Host "`nFederated identity credential (managed identity as client assertion)" -ForegroundColor Cyan

$managedIdentity = az identity show --ids $ManagedIdentityResourceId --output json 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Could not read managed identity '$ManagedIdentityResourceId'. Deploy infra/modules/identity.bicep first. $managedIdentity"
}
$managedIdentity = $managedIdentity | ConvertFrom-Json

$federatedCredential = @{
    name        = 'blinkmark-managed-identity'
    issuer      = "https://login.microsoftonline.com/$TenantId/v2.0"
    subject     = $managedIdentity.principalId
    audiences   = @('api://AzureADTokenExchange')
    description = 'Lets the API perform the On-Behalf-Of exchange using its managed identity instead of a client secret (Principle VII).'
}

if ($null -ne $apiApp -and $PSCmdlet.ShouldProcess($ApiDisplayName, 'Add federated identity credential')) {
    $existing = Invoke-Graph -Method GET -Uri "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)/federatedIdentityCredentials"
    $already = $existing.value | Where-Object { $_.name -eq $federatedCredential.name }
    if ($null -eq $already) {
        Invoke-Graph -Method POST -Uri "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)/federatedIdentityCredentials" -Body $federatedCredential | Out-Null
        Write-Host '  created' -ForegroundColor Green
    }
    else {
        Write-Host '  already present' -ForegroundColor DarkGray
    }
}

# -----------------------------------------------------------------------------
# 3. SPA registration
# -----------------------------------------------------------------------------

Write-Host "`nSPA registration: $SpaDisplayName" -ForegroundColor Cyan

$spaBody = @{
    displayName            = $SpaDisplayName
    tags                   = @("ServiceTreeId:$ServiceTreeId", 'BlinkMark')
    notes                  = "BlinkMark SPA. Service Tree id $ServiceTreeId. Authorization code flow with PKCE; holds no secret."
    signInAudience         = 'AzureADMyOrg'
    spa                    = @{
        # Authorization code flow with PKCE. The SPA holds no secret either — it cannot, and it
        # should not pretend to.
        redirectUris = @($AppOrigin, "$AppOrigin/", "$AppOrigin/auth/callback")
    }
    requiredResourceAccess = @(
        @{
            resourceAppId  = $apiApp.appId
            resourceAccess = @(
                @{ id = $filesScopeId; type = 'Scope' },
                @{ id = $commentsScopeId; type = 'Scope' }
            )
        },
        @{
            resourceAppId  = '00000003-0000-0000-c000-000000000000'
            resourceAccess = @(
                @{ id = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'; type = 'Scope' }
            )
        }
    )
}

$spaApp = Get-ApplicationByDisplayName -DisplayName $SpaDisplayName
if ($null -eq $spaApp) {
    if ($PSCmdlet.ShouldProcess($SpaDisplayName, 'Create SPA application registration')) {
        $spaApp = Invoke-Graph -Method POST -Uri 'https://graph.microsoft.com/v1.0/applications' -Body $spaBody
        Write-Host "  created appId $($spaApp.appId)" -ForegroundColor Green
    }
}
else {
    if ($PSCmdlet.ShouldProcess($SpaDisplayName, 'Update SPA application registration')) {
        Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$($spaApp.id)" -Body $spaBody | Out-Null
        Write-Host "  updated appId $($spaApp.appId)" -ForegroundColor Green
    }
}

# -----------------------------------------------------------------------------
# 4. Pre-authorize the SPA so users are not prompted to consent to their own tenant's app
# -----------------------------------------------------------------------------

if ($null -ne $apiApp -and $null -ne $spaApp -and $PSCmdlet.ShouldProcess($ApiDisplayName, 'Pre-authorize SPA client')) {
    Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)" -Body @{
        api = @{
            preAuthorizedApplications = @(
                @{
                    appId                  = $spaApp.appId
                    delegatedPermissionIds = @($filesScopeId, $commentsScopeId)
                }
            )
        }
    } | Out-Null
}

# -----------------------------------------------------------------------------
# 5. Result
# -----------------------------------------------------------------------------

$result = [ordered]@{
    tenantId                 = $TenantId
    serviceTreeId            = $ServiceTreeId
    apiClientId              = $apiApp.appId
    apiIdentifierUri         = "api://$($apiApp.appId)"
    spaClientId              = $spaApp.appId
    scopes                   = @('Files.ReadWrite', 'Comments.ReadWrite')
    federatedCredentialName  = $federatedCredential.name
    managedIdentityPrincipal = $managedIdentity.principalId
}

$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8

Write-Host ''
Write-Host 'Registrations complete. No client secret and no certificate was created.' -ForegroundColor Green
Write-Host "Identifiers written to $OutputPath (identifiers only — nothing here is a credential)." -ForegroundColor Green


# -----------------------------------------------------------------------------
# 6. Admin consent — not required
#
# BlinkMark requests exactly one Graph permission: delegated User.Read, for display-name
# resolution. Every user consents to that for themselves, so there is nothing for a tenant
# administrator to approve and no SPACE / App Admin Consent request to file.
#
# That is a deliberate design outcome rather than luck. The original notification design needed
# the Mail.Send application permission, which Microsoft's Entra mail guidance rates
# Critical / Restricted and which the Microsoft tenant does not currently grant app-only at all.
# R9 was revised to deliver notifications in-app instead, which removed the permission, the
# consent request, and the SDL / Privacy / RAI review gate that sat behind it.
#
# If email or Teams delivery is ever added, the consent path returns. Use mailbox-scoped Resource
# Specific Consent rather than a tenant-wide application permission, and for a time-bound event
# note that SPACE has a Temporary Consent path (90 days, auto-approved only for low-risk or
# event-approved permissions): https://spacetool.microsoft.com/create-temporary-consent-request
# -----------------------------------------------------------------------------

Write-Host ''
Write-Host 'No admin consent required. BlinkMark requests only delegated User.Read, which each' -ForegroundColor Green
Write-Host 'user consents to for themselves. Notifications are delivered in-app (research.md R9).' -ForegroundColor Green
Write-Host ''
