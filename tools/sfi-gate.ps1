#!/usr/bin/env pwsh
<#
.SYNOPSIS
    SFI compliance gate (T134). Fails the build if BlinkMark would ever hold a retrievable
    credential, or if any Bicep template would leave local authentication enabled.

.DESCRIPTION
    Constitution Principle VII ("Credential-Free by Default") and the SFI Safe Secrets Standard
    are not review checklists — they are build conditions. This script is the enforcement point.

    It fails, rather than warns, on two classes of regression:

      1. A Bicep template that enables (or fails to disable) local authentication on a data
         service: storage shared key, Cosmos local auth, Redis access keys, Log Analytics local
         auth, or Key Vault access policies instead of RBAC.
      2. A connection string, account key, SAS token, or client secret appearing anywhere in
         source, configuration, or pipeline definitions.

    Exit code 0 = compliant. Exit code 1 = at least one violation.

.PARAMETER RepositoryRoot
    Root of the repository to scan. Defaults to the repository containing this script.
#>
[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$violations = [System.Collections.Generic.List[string]]::new()

function Add-Violation {
    param([string] $Rule, [string] $File, [int] $Line, [string] $Detail)
    $relative = $File.Replace($root, '').TrimStart([char]'\', [char]'/')
    $violations.Add("[$Rule] $relative`:$Line — $Detail")
}

$excludedDirectories = @('\.git', 'node_modules', '\\bin\\', '\\obj\\', '\\dist\\', '\.artifacts', 'package-lock\.json')

function Get-ScannableFiles {
    param([string[]] $Include)
    Get-ChildItem -LiteralPath $root -Recurse -File -Include $Include -ErrorAction SilentlyContinue |
        Where-Object {
            $path = $_.FullName
            -not ($excludedDirectories | Where-Object { $path -match $_ })
        }
}

# ---------------------------------------------------------------------------
# Rule set 1 — local authentication must be disabled in every Bicep template.
#
# Each entry is: a marker that says "this template *declares* resource X", the property that
# must appear, and the value it must carry. A template that declares the resource without the
# property is as much a violation as one that sets it wrongly, because the platform default is
# to leave local auth on.
#
# The resource pattern matches declarations (`'type@version' = {`) only, not `existing`
# references — those carry no properties and are how one module grants a role on a resource
# another module owns.
# ---------------------------------------------------------------------------
$localAuthRules = @(
    @{
        Name       = 'SFI-ID4.2.1-storage-shared-key'
        Resource   = "'Microsoft\.Storage/storageAccounts@[^']*'\s*=\s*\{"
        Property   = 'allowSharedKeyAccess'
        MustEqual  = 'false'
        Detail     = 'Storage account must set allowSharedKeyAccess: false'
    },
    @{
        Name       = 'SFI-ID4.2.1-storage-oauth-default'
        Resource   = "'Microsoft\.Storage/storageAccounts@[^']*'\s*=\s*\{"
        Property   = 'defaultToOAuthAuthentication'
        MustEqual  = 'true'
        Detail     = 'Storage account must set defaultToOAuthAuthentication: true'
    },
    @{
        Name       = 'SFI-ID4.2.3-cosmos-local-auth'
        Resource   = "'Microsoft\.DocumentDB/databaseAccounts@[^']*'\s*=\s*\{"
        Property   = 'disableLocalAuth'
        MustEqual  = 'true'
        Detail     = 'Cosmos DB account must set disableLocalAuth: true'
    },
    @{
        Name       = 'SFI-ID4.2.7-redis-access-keys'
        Resource   = "'Microsoft\.Cache/redis@[^']*'\s*=\s*\{"
        Property   = 'disableAccessKeyAuthentication'
        MustEqual  = 'true'
        Detail     = 'Redis must set disableAccessKeyAuthentication: true'
    },
    @{
        Name       = 'SFI-log-analytics-local-auth'
        Resource   = "'Microsoft\.OperationalInsights/workspaces@[^']*'\s*=\s*\{"
        Property   = 'disableLocalAuth'
        MustEqual  = 'true'
        Detail     = 'Log Analytics workspace must set features.disableLocalAuth: true'
    },
    @{
        Name       = 'SFI-key-vault-rbac'
        Resource   = "'Microsoft\.KeyVault/vaults@[^']*'\s*=\s*\{"
        Property   = 'enableRbacAuthorization'
        MustEqual  = 'true'
        Detail     = 'Key Vault must set enableRbacAuthorization: true (no access policies)'
    }
)

$bicepFiles = Get-ScannableFiles -Include '*.bicep'

foreach ($file in $bicepFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    # @() so a single-line file still yields an array; .Count on a bare string throws under StrictMode.
    $lines = @(Get-Content -LiteralPath $file.FullName)

    foreach ($rule in $localAuthRules) {
        if ($text -notmatch $rule.Resource) { continue }

        $match = [regex]::Match($text, "$($rule.Property)\s*:\s*(?<value>[A-Za-z0-9_.]+)")
        if (-not $match.Success) {
            Add-Violation -Rule $rule.Name -File $file.FullName -Line 1 -Detail "$($rule.Detail) — property is absent"
            continue
        }
        if ($match.Groups['value'].Value -ne $rule.MustEqual) {
            $lineNumber = ($text.Substring(0, $match.Index) -split "`n").Count
            Add-Violation -Rule $rule.Name -File $file.FullName -Line $lineNumber -Detail "$($rule.Detail) — found '$($match.Groups['value'].Value)'"
        }
    }

    # Key Vault access policies are the pre-RBAC model. Their presence means someone reintroduced
    # a standing, non-auditable grant path.
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'accessPolicies\s*:\s*\[' -and $lines[$i] -notmatch 'accessPolicies\s*:\s*\[\s*\]') {
            Add-Violation -Rule 'SFI-key-vault-rbac' -File $file.FullName -Line ($i + 1) -Detail 'Key Vault accessPolicies must be empty; use RBAC role assignments'
        }
        if ($lines[$i] -match 'listKeys\s*\(|listConnectionStrings\s*\(|listAccountSas\s*\(|listServiceSas\s*\(') {
            Add-Violation -Rule 'SFI-no-key-retrieval' -File $file.FullName -Line ($i + 1) -Detail 'Template retrieves a key, connection string, or SAS. Use managed identity instead'
        }
        if ($lines[$i] -match "publicNetworkAccess\s*:\s*'?Enabled'?") {
            Add-Violation -Rule 'SFI-NS2.2.1' -File $file.FullName -Line ($i + 1) -Detail 'publicNetworkAccess must be Disabled on data services'
        }
    }
}

# ---------------------------------------------------------------------------
# Rule set 2 — no credential material anywhere in source, config, or pipelines.
#
# Patterns are deliberately shaped to catch real credentials rather than the word "secret".
# ---------------------------------------------------------------------------
$secretPatterns = @(
    @{ Name = 'storage-connection-string'; Pattern = 'DefaultEndpointsProtocol\s*=.*AccountKey\s*=' ; Detail = 'Azure Storage connection string with an account key' },
    @{ Name = 'account-key';               Pattern = 'AccountKey\s*=\s*[A-Za-z0-9+/]{40,}'          ; Detail = 'Storage account key' },
    @{ Name = 'cosmos-key';                Pattern = 'AccountEndpoint\s*=.*AccountKey\s*='          ; Detail = 'Cosmos DB connection string' },
    @{ Name = 'redis-connection-string';   Pattern = '[A-Za-z0-9-]+\.redis\.cache\.windows\.net:6380.*password\s*='; Detail = 'Redis connection string with a password' },
    @{ Name = 'sas-token';                 Pattern = '[?&]sig=[A-Za-z0-9%+/]{20,}'                  ; Detail = 'Shared Access Signature token' },
    @{ Name = 'client-secret';             Pattern = '(client_?secret|ClientSecret)\s*[:=]\s*["'']?[A-Za-z0-9~._\-]{16,}'; Detail = 'Entra application client secret' },
    @{ Name = 'service-principal-json';    Pattern = '"clientSecret"\s*:\s*"[^"]{8,}"'              ; Detail = 'Service principal credential JSON' },
    @{ Name = 'publish-profile';           Pattern = 'publishProfile|PublishSettings'               ; Detail = 'App Service publish profile (use workload identity federation)' },
    @{ Name = 'swa-deployment-token';      Pattern = 'azure_static_web_apps_api_token'              ; Detail = 'Static Web Apps deployment token (use workload identity federation)' },
    @{ Name = 'private-key';               Pattern = '-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----'; Detail = 'Private key material' }
)

# Lines that describe the prohibition rather than commit a credential must not trip the gate.
# The marker keeps the exemption explicit and greppable rather than implicit in a path allowlist.
$allowMarker = 'sfi-gate:allow'

$scanIncludes = @('*.cs', '*.ts', '*.tsx', '*.js', '*.jsx', '*.json', '*.yml', '*.yaml', '*.bicep', '*.bicepparam', '*.ps1', '*.sh', '*.props', '*.targets', '*.csproj', '*.config', '*.env')
$sourceFiles = Get-ScannableFiles -Include $scanIncludes |
    Where-Object { $_.FullName -ne $PSCommandPath }

foreach ($file in $sourceFiles) {
    $lines = @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue)
    if ($lines.Count -eq 0) { continue }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match $allowMarker) { continue }

        foreach ($pattern in $secretPatterns) {
            if ($line -match $pattern.Pattern) {
                Add-Violation -Rule "SFI-secret/$($pattern.Name)" -File $file.FullName -Line ($i + 1) -Detail $pattern.Detail
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Rule set 3 — ownership and token-validation posture.
#
# Two things that are easy to lose quietly, and expensive to notice late.
# ---------------------------------------------------------------------------

$mainBicep = Join-Path $root 'infra/main.bicep'
if (Test-Path $mainBicep) {
    $mainText = Get-Content -LiteralPath $mainBicep -Raw

    # An unowned application is one nobody patches, nobody is paged for, and nobody
    # decommissions. Requiring the parameter means a deployment cannot skip the question.
    if ($mainText -notmatch 'param\s+serviceTreeId\s+string') {
        Add-Violation -Rule 'SFI-ownership' -File $mainBicep -Line 1 -Detail 'infra/main.bicep must declare a required serviceTreeId parameter'
    }

    if ($mainText -notmatch 'serviceTreeId\s*:\s*serviceTreeId') {
        Add-Violation -Rule 'SFI-ownership' -File $mainBicep -Line 1 -Detail 'The Service Tree id must be applied as a resource tag on every resource'
    }
}

# The token handler verifies with whatever algorithm it is told to accept. The production set is
# RS256 and nothing else; the relaxation exists so the in-process test host can present
# symmetrically signed tokens through the real validation path, and it must never leave tests.
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Replace($root, '').TrimStart([char]'\', [char]'/')
    if ($relative -match '^backend[\\/]tests[\\/]') { continue }

    $lines = @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue)

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $allowMarker) { continue }

        if ($lines[$i] -match 'AdditionalValidAlgorithms\s*(:|=)\s*\[?\s*[''"]') {
            Add-Violation -Rule 'SFI-token-validation' -File $file.FullName -Line ($i + 1) -Detail 'Signing algorithms may not be relaxed outside the test host; production accepts RS256 only'
        }

        if ($lines[$i] -match 'ValidateIssuer\s*=\s*false|ValidateAudience\s*=\s*false|ValidateLifetime\s*=\s*false|ValidateIssuerSigningKey\s*=\s*false|RequireSignedTokens\s*=\s*false') {
            Add-Violation -Rule 'SFI-token-validation' -File $file.FullName -Line ($i + 1) -Detail 'Token validation cannot be disabled'
        }

        if ($lines[$i] -match 'RequireHttpsMetadata\s*=\s*false') {
            Add-Violation -Rule 'SFI-token-validation' -File $file.FullName -Line ($i + 1) -Detail 'Signing key metadata must be retrieved over HTTPS'
        }
    }
}

# ---------------------------------------------------------------------------
# Result
# ---------------------------------------------------------------------------
if ($violations.Count -gt 0) {
    Write-Host ''
    Write-Host "SFI compliance gate FAILED with $($violations.Count) violation(s):" -ForegroundColor Red
    Write-Host ''
    foreach ($violation in $violations) {
        Write-Host "  $violation" -ForegroundColor Red
    }
    Write-Host ''
    Write-Host 'Constitution Principle VII: BlinkMark holds no retrievable secret and no resource' -ForegroundColor Yellow
    Write-Host 'accepts local authentication. Fix the finding rather than suppressing the gate.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'SFI compliance gate passed: no local authentication enabled, no credential material found.' -ForegroundColor Green
exit 0
