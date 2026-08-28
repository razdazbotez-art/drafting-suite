param(
    [string]$Configuration = "Release",
    [ValidateSet("2024", "2026")]
    [string]$AutoCADYear = "2024",
    [string]$TargetFramework = "",
    [string]$AutoCADDLL = "",
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $scriptRoot = Split-Path -Parent $PSCommandPath
    return Split-Path -Parent $scriptRoot
}

function Assert-TargetMatchesYear {
    param(
        [string]$Year,
        [string]$Framework
    )

    if ($Year -eq "2024" -and $Framework -ne "net48") {
        throw "Civil 3D 2024 builds must use TargetFramework net48."
    }

    if ($Year -eq "2026" -and $Framework -ne "net10.0-windows") {
        throw "Civil 3D 2026.2.2+ builds must use TargetFramework net10.0-windows."
    }
}

$repoRoot = Resolve-RepoRoot
$project = Join-Path $repoRoot "src\DraftingSuite\DraftingSuite.csproj"
$projectRoot = Split-Path -Parent $project

if ([string]::IsNullOrWhiteSpace($TargetFramework)) {
    $TargetFramework = if ($AutoCADYear -eq "2026") { "net10.0-windows" } else { "net48" }
}

Assert-TargetMatchesYear -Year $AutoCADYear -Framework $TargetFramework

if ([string]::IsNullOrWhiteSpace($AutoCADDLL)) {
    $AutoCADDLL = if ($AutoCADYear -eq "2026") { "C:\Program Files\Autodesk\AutoCAD 2026" } else { "C:\Program Files\Autodesk\AutoCAD 2024" }
}

if (-not (Test-Path -LiteralPath (Join-Path $AutoCADDLL "AcMgd.dll"))) {
    throw "AcMgd.dll was not found in '$AutoCADDLL'. Set -AutoCADDLL to the AutoCAD/Civil 3D $AutoCADYear install folder."
}

$outputDll = Join-Path $projectRoot "bin\x64\$Configuration\$TargetFramework\DraftingSuite.dll"

if (-not $NoClean) {
    foreach ($path in @((Join-Path $projectRoot "bin"), (Join-Path $projectRoot "obj"))) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

$args = @("build", $project, "-c", $Configuration, "-f", $TargetFramework, "-p:Platform=x64", "-p:AutoCADYear=$AutoCADYear", "-p:AutoCADDLL=$AutoCADDLL")

dotnet @args
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $outputDll)) {
    throw "Build completed, but DraftingSuite.dll was not found at $outputDll"
}

$builtDll = Get-Item -LiteralPath $outputDll
$hash = (Get-FileHash -LiteralPath $builtDll.FullName -Algorithm SHA256).Hash

Write-Host ""
Write-Host "Build complete."
Write-Host "Target:"
Write-Host "  Civil 3D $AutoCADYear / $TargetFramework"
Write-Host "DLL:"
Write-Host "  $($builtDll.FullName)"
Write-Host "Timestamp:"
Write-Host "  $($builtDll.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))"
Write-Host "SHA256:"
Write-Host "  $hash"
