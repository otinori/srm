<#
.SYNOPSIS
  Publish srm.exe, Srm.Mcp.exe, Srm.PolicyEditor.exe, and Srm.McpBridgeGuest.exe, and assemble the release zip.
.DESCRIPTION
  Runs the same steps as the Release / PR Prerelease GitHub Actions workflows,
  so the exact release package can be reproduced locally.

  All three apps are published (self-contained, win-x64) into the same
  artifacts\publish\ directory. Since all are self-contained deployments of
  the same .NET 8 runtime/RID, the shared runtime DLLs are byte-identical and
  each subsequent publish simply overwrites them with the same content -- no
  duplication, no corruption. Only srm.exe, Srm.Mcp.exe, Srm.PolicyEditor.exe,
  and each app's unique dependencies end up distinct.

  Package layout (srm-<version>\):
    bin\        srm.exe, Srm.Mcp.exe, Srm.PolicyEditor.exe, and all their dependencies
    policies\   sample policies (claude-code.yaml, smoke-test.yaml, notepad-test.yaml)
    setup.ps1   first-run setup (creates ProgramData dirs, signs policies)
    usage.md    usage guide

  Output:
    artifacts\publish\           combined publish output (not zipped)
    artifacts\packages\srm-<version>\      staged package contents
    artifacts\packages\srm-<version>.zip   final release asset
.PARAMETER Version
  Optional version override (e.g. 0.1.0.0-pr12) forwarded to dotnet publish
  as -p:Version=... Without it, Directory.Build.props's plain Version is used
  for the build, and the package is named srm-<props-version>-dev to mark it
  as a local ad-hoc build rather than an official tagged release.
#>

param(
    [string]$Version = ""
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell's console codepage often doesn't match dotnet CLI's
# localized (Japanese, etc.) output, which garbles messages like "Restoring
# packages...". Switching the codepage mid-session (chcp) is unreliable and
# was observed to duplicate characters instead of fixing them. Sidestep the
# whole problem by forcing dotnet's own CLI messages to plain English, which
# is codepage-invariant.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

Set-Location (Join-Path $PSScriptRoot '..')

$versionArgs = @()
if ($Version -ne '') { $versionArgs = @("-p:Version=$Version") }

if (Test-Path artifacts\publish) { Remove-Item artifacts\publish -Recurse -Force }
New-Item -ItemType Directory -Path artifacts\packages -Force | Out-Null

Write-Host "=== Publishing srm CLI ==="
dotnet publish src\Srm.Cli\Srm.Cli.csproj -c Release -r win-x64 --self-contained @versionArgs -o artifacts\publish
if ($LASTEXITCODE -ne 0) { throw "srm publish failed" }

Write-Host "=== Publishing Srm.Mcp ==="
dotnet publish src\Srm.Mcp\Srm.Mcp.csproj -c Release -r win-x64 --self-contained @versionArgs -o artifacts\publish
if ($LASTEXITCODE -ne 0) { throw "Srm.Mcp publish failed" }

Write-Host "=== Publishing Srm.PolicyEditor ==="
dotnet publish src\Srm.PolicyEditor\Srm.PolicyEditor.csproj -c Release -r win-x64 --self-contained @versionArgs -o artifacts\publish
if ($LASTEXITCODE -ne 0) { throw "Srm.PolicyEditor publish failed" }

Write-Host "=== Publishing Srm.McpBridgeGuest ==="
dotnet publish src\Srm.McpBridgeGuest\Srm.McpBridgeGuest.csproj -c Release -r win-x64 --self-contained @versionArgs -o artifacts\publish
if ($LASTEXITCODE -ne 0) { throw "Srm.McpBridgeGuest publish failed" }

# DC-018/DC-026: combining several self-contained publishes into one bin\
# folder relies on every project resolving shared framework-replacement
# packages (System.Text.Json, System.IO.Pipelines) to the exact same
# version. If any project publishing into this folder doesn't have the
# same PackageReference pin as the others, dotnet publish's incremental
# copy can silently leave a mismatched DLL in place (an earlier project's
# file isn't overwritten by a later one, or vice versa) and the combined
# app crashes at runtime with a FileNotFoundException that gives no hint
# this is the cause. DC-018 fixed this once for the first 3 apps; DC-026
# found it had silently regressed via a 4th app (Srm.PolicyEditor) that
# never received the pin. Catch this class of bug here, at packaging time,
# instead of discovering it during real-machine Tier2/Channel D testing.
Write-Host "=== Verifying shared dependency versions ==="
$mismatches = @()
$watchedLibraries = @('System.Text.Json', 'System.IO.Pipelines')
Get-ChildItem artifacts\publish -Filter '*.deps.json' | ForEach-Object {
    $deps = Get-Content $_.FullName -Raw | ConvertFrom-Json
    $libraryNames = $deps.libraries.PSObject.Properties.Name
    foreach ($libName in $watchedLibraries) {
        $matchingKey = $libraryNames | Where-Object { $_ -like "$libName/*" } | Select-Object -First 1
        if (-not $matchingKey) { continue }

        $expectedMajor = [int]($matchingKey.Split('/')[1].Split('.')[0])
        $dllPath = Join-Path artifacts\publish "$libName.dll"
        if (-not (Test-Path $dllPath)) { continue }

        $actualVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path $dllPath).Path).Version
        if ($actualVersion.Major -ne $expectedMajor) {
            $mismatches += "$($_.Name) expects $libName major version $expectedMajor, but artifacts\publish\$libName.dll is actually version $actualVersion"
        }
    }
}
if ($mismatches.Count -gt 0) {
    Write-Host ''
    Write-Host '*** DEPENDENCY VERSION MISMATCH DETECTED (see DC-018 / DC-026) ***' -ForegroundColor Red
    $mismatches | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ''
    throw 'Combined publish folder has inconsistent shared dependency versions. Check that every project publishing into artifacts\publish has the same PackageReference version pin for the libraries listed above (see DC-018.yaml / DC-026.yaml).'
}
Write-Host 'All shared dependency versions consistent.'

if ($Version -ne '') {
    $pkgName = "srm-$Version"
} else {
    $propsVersion = (Select-Xml -Path Directory.Build.props -XPath '//Version').Node.InnerText
    $pkgName = "srm-$propsVersion-dev"
}
$pkg = "artifacts\packages\$pkgName"
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
if (Test-Path "$pkg.zip") { Remove-Item "$pkg.zip" -Force }

New-Item -ItemType Directory -Path "$pkg\bin" -Force | Out-Null
New-Item -ItemType Directory -Path "$pkg\policies" -Force | Out-Null
Copy-Item artifacts\publish\* $pkg\bin\ -Recurse
Copy-Item setup.ps1 $pkg\
Copy-Item manual\usage.md $pkg\
Copy-Item policies\claude-code.yaml, policies\smoke-test.yaml, policies\notepad-test.yaml $pkg\policies\
Compress-Archive -Path $pkg -DestinationPath "$pkg.zip"

Write-Host ""
Write-Host "*** package succeeded ***"
Write-Host "Output: $pkg.zip"
