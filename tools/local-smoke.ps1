# Local smoke test.
#
# Exercises the rules that matter most by hand, against the running local host. Not a replacement
# for the test suite — a way to see, with your own eyes, that the promises hold in a live process.
#
#   dotnet run --project backend/tools/BlinkMark.LocalHost
#   pwsh tools/local-smoke.ps1

#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$api = 'http://localhost:5080'

function Get-Token([string] $user) {
    (Invoke-RestMethod "$api/dev/token?user=$user").accessToken
}

# Returns the status of a request rather than throwing, so a refusal can be asserted as easily as
# a success. A test that can only observe success cannot prove that anything is denied.
function Get-Status([scriptblock] $request) {
    try {
        & $request | Out-Null
        return 200
    } catch {
        if ($_.Exception.Response) { return [int] $_.Exception.Response.StatusCode }
        throw
    }
}

function Assert([string] $label, [int] $actual, [int] $expected) {
    $ok = $actual -eq $expected
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1,-52} {2} (expected {3})" -f $mark, $label, $actual, $expected)
    if (-not $ok) { $script:failed++ }
}

$script:failed = 0

$alice = @{ Authorization = "Bearer $(Get-Token alice)" }
$bob = @{ Authorization = "Bearer $(Get-Token bob)" }

$sample = Join-Path ([IO.Path]::GetTempPath()) 'blinkmark-sample.md'
Set-Content $sample "# Draft`n`nThe ingest path is the bottleneck.`n`nRetention is enforced by the platform." -Encoding utf8

$file = Invoke-RestMethod "$api/api/files" -Method Post -Headers $alice -Form @{ file = Get-Item $sample }

Write-Host "`nUploaded $($file.id), expires $($file.expiresAt)`n"

Write-Host 'Access control'
Assert 'no token is refused' (Get-Status { Invoke-RestMethod "$api/api/files" }) 401
Assert 'a tenant member can read the file' (Get-Status { Invoke-RestMethod "$api/api/files/$($file.id)" -Headers $bob }) 200
Assert 'a non-owner cannot delete it' (Get-Status { Invoke-RestMethod "$api/api/files/$($file.id)" -Method Delete -Headers $bob }) 403
Assert 'a non-owner cannot download it' (Get-Status { Invoke-RestMethod "$api/api/files/$($file.id)/download" -Headers $bob }) 403

Write-Host "`nRetention"
$body = { param($days) (@{ expiresAt = (Get-Date).ToUniversalTime().AddDays($days) } | ConvertTo-Json) }
Assert 'the owner can extend within the ceiling' (Get-Status {
    Invoke-RestMethod "$api/api/files/$($file.id)/retention" -Method Patch -Headers $alice `
        -ContentType 'application/json' -Body (& $body 7)
}) 200
Assert 'past 30 days from upload is refused' (Get-Status {
    Invoke-RestMethod "$api/api/files/$($file.id)/retention" -Method Patch -Headers $alice `
        -ContentType 'application/json' -Body (& $body 31)
}) 422
Assert 'a non-owner cannot extend' (Get-Status {
    Invoke-RestMethod "$api/api/files/$($file.id)/retention" -Method Patch -Headers $bob `
        -ContentType 'application/json' -Body (& $body 2)
}) 403

Write-Host "`nCommenting"
$projection = (Invoke-RestMethod "$api/api/files/$($file.id)/content" -Headers $alice).text
$quote = 'The ingest path is the bottleneck.'
$start = $projection.IndexOf($quote)

$comment = Invoke-RestMethod "$api/api/files/$($file.id)/comments" -Method Post -Headers $bob `
    -ContentType 'application/json' -Body (@{
        body   = 'Can we quantify this?'
        anchor = @{
            kind = 'text'; exact = $quote
            prefix = $projection.Substring([Math]::Max(0, $start - 32), [Math]::Min(32, $start))
            suffix = ''
            start = $start; end = $start + $quote.Length
        }
    } | ConvertTo-Json -Depth 5)

Assert 'the comment anchored to its passage' ($(if ($comment.anchorState -eq 'anchored') { 200 } else { 0 })) 200
Assert 'the author came from the token, not the body' ($(if ($comment.authorDisplayName -eq 'Bob') { 200 } else { 0 })) 200
Assert 'the author can edit their own comment' (Get-Status {
    Invoke-RestMethod "$api/api/files/$($file.id)/comments/$($comment.id)" -Method Patch -Headers $bob `
        -ContentType 'application/json' -Body (@{ body = 'Edited.' } | ConvertTo-Json)
}) 200
Assert 'someone else cannot edit it' (Get-Status {
    Invoke-RestMethod "$api/api/files/$($file.id)/comments/$($comment.id)" -Method Patch -Headers $alice `
        -ContentType 'application/json' -Body (@{ body = 'Not mine to edit.' } | ConvertTo-Json)
}) 403

Write-Host "`nPreview isolation"
$previewUri = [uri] (Invoke-RestMethod "$api/api/files/$($file.id)" -Headers $alice).previewUrl
Assert 'the preview is served from a different origin' `
    ($(if ($previewUri.Authority -ne ([uri] $api).Authority) { 200 } else { 0 })) 200

$preview = Invoke-WebRequest $previewUri -SkipHttpErrorCheck
Assert 'the preview renders with a token' ([int] $preview.StatusCode) 200
Assert 'it forbids all network access' `
    ($(if ($preview.Headers['Content-Security-Policy'] -match "default-src 'none'") { 200 } else { 0 })) 200
Assert 'the preview refuses to be read without a token' `
    (Get-Status { Invoke-RestMethod "$($previewUri.Scheme)://$($previewUri.Authority)$($previewUri.AbsolutePath)" }) 401

Write-Host "`nSanitization"
$hostile = 'backend/tests/fixtures/hostile/inline-script.html'
if (Test-Path $hostile) {
    $evil = Invoke-RestMethod "$api/api/files" -Method Post -Headers $alice -Form @{ file = Get-Item $hostile }
    $rendered = (Invoke-WebRequest ([uri] (Invoke-RestMethod "$api/api/files/$($evil.id)" -Headers $alice).previewUrl)).Content
    Assert 'no <script> survives the sanitizer' ($(if ($rendered -notmatch '<script') { 200 } else { 0 })) 200
    Assert 'no inline event handler survives' ($(if ($rendered -notmatch 'onerror=|onload=') { 200 } else { 0 })) 200
} else {
    Write-Host "  [SKIP] hostile fixtures not found at $hostile"
}

Write-Host ''
if ($script:failed -gt 0) {
    Write-Host "$($script:failed) check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All local checks passed.' -ForegroundColor Green
