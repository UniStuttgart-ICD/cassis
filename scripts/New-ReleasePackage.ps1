[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$project = Join-Path $repoRoot "src\Cassis\Cassis.csproj"
$framework = "net8.0-windows"
$outputDir = Join-Path $artifactsRoot "bin\Cassis\$Configuration\$framework"
$releaseDir = Join-Path $artifactsRoot "release"
$stageDir = Join-Path $releaseDir "Cassis"
$archive = Join-Path $releaseDir "Cassis.zip"

function Remove-ArtifactDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $artifactPrefix = $artifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (!$resolved.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside $artifactsRoot"
    }

    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

if (!$NoBuild) {
    Remove-ArtifactDirectory -Path $outputDir
    & dotnet build $project --configuration $Configuration --framework $framework --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed."
    }
}

$plugin = Join-Path $outputDir "Cassis.gha"
if (!(Test-Path -LiteralPath $plugin)) {
    throw "Release output is missing Cassis.gha: $plugin"
}

Remove-ArtifactDirectory -Path $releaseDir
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Copy-Item -LiteralPath $plugin -Destination $stageDir
Get-ChildItem -LiteralPath $outputDir -Filter "*.dll" -File |
    Copy-Item -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md") -Destination $stageDir

$requiredRuntime = Join-Path $stageDir "System.Text.Json.dll"
if (!(Test-Path -LiteralPath $requiredRuntime)) {
    throw "Release output is missing System.Text.Json.dll."
}

Compress-Archive -Path $stageDir -DestinationPath $archive -CompressionLevel Optimal

& (Join-Path $PSScriptRoot "Test-PublicRelease.ps1") -Archive $archive
if ($LASTEXITCODE -ne 0) {
    throw "Release verification failed."
}

$hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Host "Release archive: $archive"
Write-Host "SHA256: $($hash.Hash.ToLowerInvariant())"
