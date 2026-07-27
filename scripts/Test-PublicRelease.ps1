[CmdletBinding()]
param(
    [string]$Archive,
    [string]$YakArchive,
    [switch]$CheckHistory,
    [string]$HistoryRef
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$restrictedTerms = @(
    -join ([char[]](65, 66, 120, 77)),
    -join ([char[]](82, 65, 68, 114))
)
$legacyProductNames = @(
    -join ([char[]](71, 114, 97, 115, 115, 104, 111, 112, 112, 101, 114, 77, 67, 80)),
    -join ([char[]](103, 114, 97, 115, 115, 104, 111, 112, 112, 101, 114, 95, 109, 99, 112)),
    -join ([char[]](103, 114, 97, 115, 115, 104, 111, 112, 112, 101, 114, 45, 109, 99, 112)),
    -join ([char[]](71, 114, 97, 115, 115, 104, 111, 112, 112, 101, 114, 32, 77, 67, 80))
)
$privateKeyExtensions = @(".snk", ".pfx", ".p12", ".pem", ".key")
$privateKeyMarkers = @(
    "LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0t",
    "LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQ==",
    "LS0tLS1CRUdJTiBFQyBQUklWQVRFIEtFWS0tLS0t",
    "LS0tLS1CRUdJTiBPUEVOU1NIIFBSSVZBVEUgS0VZLS0tLS0="
) | ForEach-Object {
    [System.Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($_))
}
$scanEncodings = @(
    [System.Text.Encoding]::GetEncoding(28591),
    [System.Text.Encoding]::UTF8,
    [System.Text.Encoding]::Unicode,
    [System.Text.Encoding]::BigEndianUnicode
)

# Build restricted signatures at runtime so the verifier does not flag itself.
function Assert-PathAllowed {
    param([Parameter(Mandatory)][string]$Path)

    foreach ($term in $restrictedTerms) {
        if ($Path.IndexOf($term, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Restricted path found: $Path"
        }
    }

    if ($privateKeyExtensions -contains [System.IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        throw "Private-key file found: $Path"
    }
}

function Assert-FileAllowed {
    param([Parameter(Mandatory)][string]$Path)

    Assert-PathAllowed -Path $Path
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    foreach ($encoding in $scanEncodings) {
        $text = $encoding.GetString($bytes)
        foreach ($term in $restrictedTerms) {
            if ($text.IndexOf($term, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Restricted content found in $Path"
            }
        }

        foreach ($marker in $privateKeyMarkers) {
            if ($text.IndexOf($marker, [System.StringComparison]::Ordinal) -ge 0) {
                throw "Private-key content found in $Path"
            }
        }
    }
}

function Assert-LegacyProductNameAbsent {
    param([Parameter(Mandatory)][string]$Path)

    foreach ($name in $legacyProductNames) {
        if ($Path.IndexOf($name, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Legacy product name found in path: $Path"
        }
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    foreach ($encoding in $scanEncodings) {
        $text = $encoding.GetString($bytes)
        foreach ($name in $legacyProductNames) {
            if ($text.IndexOf($name, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Legacy product name found in $Path"
            }
        }
    }
}

Push-Location $repoRoot
try {
    $trackedFiles = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to list tracked files."
    }

    foreach ($relativePath in $trackedFiles) {
        $fullPath = Join-Path $repoRoot $relativePath
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            Assert-FileAllowed -Path $fullPath
            Assert-LegacyProductNameAbsent -Path $fullPath
        }
    }

    [xml]$project = Get-Content -LiteralPath "src\Cassis\Cassis.csproj" -Raw
    $projectVersion = ([string]$project.Project.PropertyGroup.Version).Trim()
    $manifestText = Get-Content -LiteralPath "manifest.yml" -Raw
    $manifestVersion = [regex]::Match($manifestText, "(?m)^version:\s*(\S+)\s*$").Groups[1].Value
    $pythonText = Get-Content -LiteralPath "pyproject.toml" -Raw
    $pythonVersion = [regex]::Match($pythonText, '(?m)^version\s*=\s*"([^"]+)"\s*$').Groups[1].Value

    if (!$projectVersion -or $projectVersion -ne $manifestVersion -or $projectVersion -ne $pythonVersion) {
        throw "Version metadata is inconsistent: project=$projectVersion manifest=$manifestVersion tests=$pythonVersion"
    }

    $canonicalSkill = (Get-Content -LiteralPath "skills\cassis-setup\SKILL.md" -Raw).Trim()
    $skillCopies = @(
        ".agents\skills\cassis-setup\SKILL.md",
        ".claude\skills\cassis-setup\SKILL.md",
        ".github\skills\cassis-setup\SKILL.md"
    )
    foreach ($skillCopy in $skillCopies) {
        $copyText = Get-Content -LiteralPath $skillCopy -Raw
        $copyText = $copyText -replace '^<!-- Canonical source: skills/cassis-setup/SKILL\.md -->\r?\n', ''
        $copyText = $copyText.Replace(
            "Consult [the MCP configuration guide](../../../skills/cassis-setup/references/mcp-config-guide.md)",
            "Consult [references/mcp-config-guide.md](references/mcp-config-guide.md)")
        if ($copyText.Trim() -ne $canonicalSkill) {
            throw "Agent skill copy is out of sync: $skillCopy"
        }
    }

    if ($CheckHistory) {
        $commits = if ($HistoryRef) {
            @(& git rev-list $HistoryRef)
        }
        else {
            @(& git rev-list --all)
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to list Git history."
        }

        $historyRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cassis-history-check-" + [guid]::NewGuid())
        try {
            New-Item -ItemType Directory -Path $historyRoot | Out-Null
            foreach ($commit in $commits) {
                $paths = @(& git ls-tree -r --name-only $commit)
                foreach ($path in $paths) {
                    Assert-PathAllowed -Path $path
                }

                $commitArchive = Join-Path $historyRoot "$commit.zip"
                $commitRoot = Join-Path $historyRoot $commit
                try {
                    & git archive --format=zip "--output=$commitArchive" $commit
                    if ($LASTEXITCODE -ne 0) {
                        throw "Unable to archive commit $commit."
                    }

                    Expand-Archive -LiteralPath $commitArchive -DestinationPath $commitRoot
                    Get-ChildItem -LiteralPath $commitRoot -Recurse -File |
                        ForEach-Object { Assert-FileAllowed -Path $_.FullName }
                }
                finally {
                    Remove-Item -LiteralPath $commitArchive -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath $commitRoot -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
        finally {
            if (Test-Path -LiteralPath $historyRoot) {
                Remove-Item -LiteralPath $historyRoot -Recurse -Force
            }
        }
    }
}
finally {
    Pop-Location
}

if ($Archive) {
    $archivePath = [System.IO.Path]::GetFullPath($Archive)
    if (!(Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Release archive not found: $archivePath"
    }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cassis-release-check-" + [guid]::NewGuid())
    try {
        Expand-Archive -LiteralPath $archivePath -DestinationPath $tempRoot
        $packageRoot = Join-Path $tempRoot "Cassis"
        $requiredFiles = @(
            "Cassis.gha",
            "System.Text.Json.dll",
            "LICENSE",
            "THIRD-PARTY-NOTICES.md"
        )

        foreach ($requiredFile in $requiredFiles) {
            if (!(Test-Path -LiteralPath (Join-Path $packageRoot $requiredFile) -PathType Leaf)) {
                throw "Release archive is missing Cassis\$requiredFile"
            }
        }

        $unexpectedFiles = Get-ChildItem -LiteralPath $packageRoot -File |
            Where-Object { $_.Extension -in @(".pdb", ".json") }
        if ($unexpectedFiles) {
            throw "Release archive contains build-only files: $($unexpectedFiles.Name -join ', ')"
        }

        Get-ChildItem -LiteralPath $tempRoot -Recurse -File |
            ForEach-Object {
                Assert-FileAllowed -Path $_.FullName
                Assert-LegacyProductNameAbsent -Path $_.FullName
            }

        $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
            (Join-Path $packageRoot "Cassis.gha")).ProductVersion
        if (!$productVersion.StartsWith($projectVersion, [System.StringComparison]::Ordinal)) {
            throw "Cassis.gha version $productVersion does not match $projectVersion."
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

if ($YakArchive) {
    $yakArchivePath = [System.IO.Path]::GetFullPath($YakArchive)
    if (!(Test-Path -LiteralPath $yakArchivePath -PathType Leaf)) {
        throw "Yak package not found: $yakArchivePath"
    }

    $expectedYakName = "cassis-$projectVersion-rh8_0-win.yak"
    if ([System.IO.Path]::GetFileName($yakArchivePath) -ne $expectedYakName) {
        throw "Unexpected Yak package name: $([System.IO.Path]::GetFileName($yakArchivePath))"
    }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cassis-yak-check-" + [guid]::NewGuid())
    try {
        New-Item -ItemType Directory -Path $tempRoot | Out-Null
        $zipPath = Join-Path $tempRoot "package.zip"
        $packageRoot = Join-Path $tempRoot "package"
        Copy-Item -LiteralPath $yakArchivePath -Destination $zipPath
        Expand-Archive -LiteralPath $zipPath -DestinationPath $packageRoot

        $requiredFiles = @(
            "manifest.yml",
            "logo\cassis_logo.png",
            "LICENSE",
            "THIRD-PARTY-NOTICES.md",
            "net48\Cassis.gha",
            "net48\System.Text.Json.dll",
            "net8.0\Cassis.gha",
            "net8.0\System.Text.Json.dll",
            "net8.0-windows\Cassis.gha",
            "net8.0-windows\System.Text.Json.dll"
        )
        foreach ($requiredFile in $requiredFiles) {
            if (!(Test-Path -LiteralPath (Join-Path $packageRoot $requiredFile) -PathType Leaf)) {
                throw "Yak package is missing $requiredFile"
            }
        }

        $unexpectedFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
            Where-Object { $_.Extension -in @(".pdb", ".json") }
        if ($unexpectedFiles) {
            throw "Yak package contains build-only files: $($unexpectedFiles.Name -join ', ')"
        }

        Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
            ForEach-Object {
                Assert-FileAllowed -Path $_.FullName
                Assert-LegacyProductNameAbsent -Path $_.FullName
            }

        $yakManifest = Get-Content -LiteralPath (Join-Path $packageRoot "manifest.yml") -Raw
        $yakVersion = [regex]::Match($yakManifest, "(?m)^version:\s*(\S+)\s*$").Groups[1].Value
        if ($yakVersion -ne $projectVersion) {
            throw "Yak manifest version $yakVersion does not match $projectVersion."
        }

        foreach ($framework in @("net48", "net8.0", "net8.0-windows")) {
            $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
                (Join-Path $packageRoot "$framework\Cassis.gha")).ProductVersion
            if (!$productVersion.StartsWith($projectVersion, [System.StringComparison]::Ordinal)) {
                throw "$framework Cassis.gha version $productVersion does not match $projectVersion."
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

Write-Host "Public release verification passed."
$global:LASTEXITCODE = 0
