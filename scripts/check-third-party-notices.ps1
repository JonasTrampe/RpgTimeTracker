<#
.SYNOPSIS
    Fails if any NuGet PackageReference in a shipped project isn't mentioned
    anywhere in THIRD-PARTY-NOTICES.txt.

.DESCRIPTION
    See docs/legal-todo.md's "Ongoing: whenever a new NuGet package is
    added" checklist - this is the CI half of that habit, catching a
    forgotten attribution entry instead of relying on memory. Only checks
    the shipped app/library projects (Host, Client, Shared); RpgTimeTracker.Tests'
    packages (xunit, coverlet, Avalonia.Headless, ...) are dev/test-only and
    never ship in a built app, so they're intentionally excluded.

    A package "counts" as covered if its exact name appears anywhere in the
    notices file - the file lists packages either individually or grouped
    under one component entry (e.g. "Avalonia, Avalonia.Desktop, ..."), so a
    simple substring match is enough and avoids over-fitting to one layout.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$noticesPath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt'
$notices = Get-Content -Raw -Path $noticesPath

$shippedProjects = @(
    'RpgTimeTracker\RpgTimeTracker.csproj'
    'RpgTimeTracker.PlayerClient\RpgTimeTracker.PlayerClient.csproj'
    'RpgTimeTracker.Shared\RpgTimeTracker.Shared.csproj'
) | ForEach-Object { Join-Path $repoRoot $_ }

$packageNames = foreach ($projectPath in $shippedProjects) {
    ([xml](Get-Content -Raw -Path $projectPath)).Project.ItemGroup.PackageReference |
        Where-Object { $_ } |
        ForEach-Object { $_.Include }
}
$packageNames = $packageNames | Sort-Object -Unique

$missing = $packageNames | Where-Object { $notices -notlike "*$_*" }

if ($missing.Count -gt 0) {
    Write-Host "The following NuGet packages are referenced by a shipped project but not mentioned in THIRD-PARTY-NOTICES.txt:"
    foreach ($name in $missing) { Write-Host "  - $name" }
    Write-Host ""
    Write-Host "See docs/legal-todo.md for what to add (license check, notices entry, copyleft caution)."
    exit 1
}

Write-Host "All $($packageNames.Count) shipped-project NuGet packages are mentioned in THIRD-PARTY-NOTICES.txt."
