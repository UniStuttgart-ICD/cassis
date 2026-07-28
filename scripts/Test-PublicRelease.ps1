[CmdletBinding()]
param(
    [string]$Archive,
    [string]$YakArchive,
    [ValidateSet("win", "mac")]
    [string]$YakPlatform,
    [switch]$CheckHistory,
    [string]$HistoryRef
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$restrictedTerms = @(
    -join ([char[]](65, 66, 120, 77)),
    -join ([char[]](82, 65, 68, 114))
)
$legacyProductPart = -join ([char[]](71, 114, 97, 115, 115, 104, 111, 112, 112, 101, 114))
$protocolPart = -join ([char[]](77, 67, 80))
$restrictedPatterns = @(
    [regex]::new(
        "$([regex]::Escape($legacyProductPart))[^A-Za-z0-9]*$([regex]::Escape($protocolPart))",
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase),
    [regex]::new(
        "$([regex]::Escape($protocolPart))[^A-Za-z0-9]*$([regex]::Escape($legacyProductPart))",
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
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
$privateBuildPaths = @(
    $repoRoot,
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
) |
    Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object {
        $path = $_.TrimEnd('\', '/')
        $path
        $path.Replace('\', '/')
        $path.Replace('/', '\')
    } |
    Select-Object -Unique

# Build restricted signatures at runtime so the verifier does not flag itself.
function Assert-PathAllowed {
    param([Parameter(Mandatory)][string]$Path)

    foreach ($term in $restrictedTerms) {
        if ($Path.IndexOf($term, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Restricted path found: $Path"
        }
    }

    foreach ($pattern in $restrictedPatterns) {
        if ($pattern.IsMatch($Path)) {
            throw "Restricted path found: $Path"
        }
    }

    if ($privateKeyExtensions -contains [System.IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        throw "Private-key file found: $Path"
    }
}

function Assert-TextAllowed {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Source
    )

    foreach ($term in $restrictedTerms) {
        if ($Text.IndexOf($term, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Restricted content found in $Source"
        }
    }

    foreach ($pattern in $restrictedPatterns) {
        if ($pattern.IsMatch($Text)) {
            throw "Restricted content found in $Source"
        }
    }

    foreach ($privateBuildPath in $privateBuildPaths) {
        if ($Text.IndexOf($privateBuildPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Private build path found in $Source"
        }
    }
}

function Assert-FileAllowed {
    param([Parameter(Mandatory)][string]$Path)

    Assert-PathAllowed -Path $Path
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    foreach ($encoding in $scanEncodings) {
        $text = $encoding.GetString($bytes)
        Assert-TextAllowed -Text $text -Source $Path

        foreach ($marker in $privateKeyMarkers) {
            if ($text.IndexOf($marker, [System.StringComparison]::Ordinal) -ge 0) {
                throw "Private-key content found in $Path"
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
                $commitMetadata = (& git cat-file commit $commit) -join "`n"
                if ($LASTEXITCODE -ne 0) {
                    throw "Unable to inspect commit metadata for $commit."
                }
                Assert-TextAllowed -Text $commitMetadata -Source "commit $commit"

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

        $annotatedTags = @(& git for-each-ref refs/tags --format="%(objecttype) %(objectname)") |
            Where-Object { $_.StartsWith("tag ") } |
            ForEach-Object { $_.Substring(4) }
        foreach ($tagObject in $annotatedTags) {
            $tagMetadata = (& git cat-file tag $tagObject) -join "`n"
            if ($LASTEXITCODE -ne 0) {
                throw "Unable to inspect tag metadata for $tagObject."
            }
            Assert-TextAllowed -Text $tagMetadata -Source "tag $tagObject"
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

    $yakFileName = [System.IO.Path]::GetFileName($yakArchivePath)
    if (!$YakPlatform) {
        $platformMatch = [regex]::Match($yakFileName, "-(win|mac)\.yak$")
        if (!$platformMatch.Success) {
            throw "Unable to infer Yak platform from $yakFileName"
        }
        $YakPlatform = $platformMatch.Groups[1].Value
    }

    $expectedFramework = if ($YakPlatform -eq "win") { "net8.0-windows" } else { "net8.0" }
    $unexpectedFrameworks = @("net48", "net8.0", "net8.0-windows") |
        Where-Object { $_ -ne $expectedFramework }
    $expectedYakName = "cassis-$projectVersion-rh8_0-$YakPlatform.yak"
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
            "$expectedFramework\Cassis.gha",
            "$expectedFramework\System.Text.Json.dll"
        )
        foreach ($requiredFile in $requiredFiles) {
            if (!(Test-Path -LiteralPath (Join-Path $packageRoot $requiredFile) -PathType Leaf)) {
                throw "Yak package is missing $requiredFile"
            }
        }
        foreach ($unexpectedFramework in $unexpectedFrameworks) {
            if (Test-Path -LiteralPath (Join-Path $packageRoot $unexpectedFramework)) {
                throw "$YakPlatform Yak package contains unexpected framework $unexpectedFramework"
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
            }

        $yakManifest = Get-Content -LiteralPath (Join-Path $packageRoot "manifest.yml") -Raw
        $yakVersion = [regex]::Match($yakManifest, "(?m)^version:\s*(\S+)\s*$").Groups[1].Value
        if ($yakVersion -ne $projectVersion) {
            throw "Yak manifest version $yakVersion does not match $projectVersion."
        }
        $manifestPlatform = [regex]::Match($yakManifest, "(?m)^platform:\s*(\S+)\s*$").Groups[1].Value
        if ($manifestPlatform -ne $YakPlatform) {
            throw "Yak manifest platform $manifestPlatform does not match $YakPlatform."
        }

        $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
            (Join-Path $packageRoot "$expectedFramework\Cassis.gha")).ProductVersion
        if (!$productVersion.StartsWith($projectVersion, [System.StringComparison]::Ordinal)) {
            throw "$expectedFramework Cassis.gha version $productVersion does not match $projectVersion."
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
