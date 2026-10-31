[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $EngineRoot,
    [switch] $ForceRecompileShaders
)

$ErrorActionPreference = 'Stop'
$enginePath = (Resolve-Path -LiteralPath $EngineRoot).Path.TrimEnd('\', '/')
$repoPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$payloadPath = Join-Path $repoPath 'game/mount/titanfall2'
$destination = Join-Path $enginePath 'mount/titanfall2'
if (!(Test-Path -LiteralPath (Join-Path $enginePath 'bin/managed/Sandbox.Engine.dll'))) {
    throw 'EngineRoot must contain bin/managed/Sandbox.Engine.dll.'
}
if (!(Test-Path -LiteralPath (Join-Path $enginePath 'bin/managed/ShaderCompiler.exe'))) {
    throw 'EngineRoot must contain bin/managed/ShaderCompiler.exe to compile the mount shaders for that SDK.'
}
if ($enginePath -eq (Join-Path $repoPath 'game')) {
    throw 'Use the regular engine build for the source checkout. This command targets an installed Steam SDK.'
}

# Do not replace an assembly while that installation has it loaded.
$running = Get-Process -Name 'sbox', 'sbox-dev', 'ShaderCompiler' -ErrorAction SilentlyContinue | Where-Object {
    if ($_.HasExited) { return $false }
    if (!$_.Path) { throw "Cannot inspect s&box process $($_.Id). Close it before deploying." }
    $_.Path.StartsWith($enginePath + '\', [StringComparison]::OrdinalIgnoreCase)
}
if ($running) { throw "Close the Steam s&box process(es) before deployment: $($running.Id -join ', ')." }
foreach ($name in 'titanfall2.dll', 'titanfall2_lzham.dll', 'titanfall2_miles.dll') {
    $existing = Join-Path $destination $name
    if (Test-Path -LiteralPath $existing) {
        $probe = [IO.File]::Open($existing, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $probe.Dispose()
    }
}

$project = Join-Path $PSScriptRoot 'Build/Steam.csproj'
& dotnet restore $project --configfile (Join-Path $PSScriptRoot 'Build/NuGet.Config') "-p:EngineRoot=$enginePath"
if ($LASTEXITCODE -ne 0) { throw 'Steam SDK restore failed; nothing deployed.' }
& dotnet build $project -c Release --no-restore "-p:EngineRoot=$enginePath"
if ($LASTEXITCODE -ne 0) { throw 'Steam SDK build failed; nothing deployed.' }
$assembly = Join-Path $PSScriptRoot 'Build/bin/Release/net10.0/Sandbox.Mounting.Titanfall2.dll'
$files = [ordered]@{
    'titanfall2.dll' = $assembly
    'titanfall2_lzham.dll' = (Join-Path $payloadPath 'titanfall2_lzham.dll')
    'titanfall2_miles.dll' = (Join-Path $payloadPath 'titanfall2_miles.dll')
}
$shaderPath = Join-Path $payloadPath 'Assets/shaders'
# Ship sources and every include, not source-SDK compiled shader caches.
foreach ($file in Get-ChildItem -LiteralPath $shaderPath -File -Recurse) {
    if ($file.Extension -notin '.shader', '.hlsl') { continue }
    $relative = [IO.Path]::GetRelativePath($shaderPath, $file.FullName)
    if ($relative.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase) -or
        $relative.StartsWith('tests\', [StringComparison]::OrdinalIgnoreCase)) { continue }
    $files[('Assets/shaders/' + $relative)] = $file.FullName
}
foreach ($source in $files.Values) {
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing payload: $source" }
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backup = Join-Path $enginePath "mount-backups/titanfall2/$stamp"
if (Test-Path -LiteralPath $destination) {
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Copy-Item -LiteralPath $destination -Destination (Join-Path $backup 'mount') -Recurse
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$writtenTargets = [System.Collections.Generic.List[string]]::new()
$destinationPrefix = [IO.Path]::GetFullPath($destination).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
try {
    $manifest = foreach ($entry in $files.GetEnumerator()) {
        $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.Key))
        if (!$target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Deployment payload escapes the mount directory: $($entry.Key)"
        }
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        # Record before Copy-Item so even a partial failed copy is rolled back.
        $writtenTargets.Add($target)
        Copy-Item -LiteralPath $entry.Value -Destination $target -Force
        $expected = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) {
            throw "Deployment verification failed: $target. Backup: $backup"
        }
        [ordered]@{ path = $entry.Key; sha256 = $expected }
    }
    $shaderFiles = @($files.Keys | Where-Object { $_.EndsWith('.shader') } | ForEach-Object { Join-Path $destination $_ })
    Push-Location -LiteralPath $enginePath
    try {
        # Let the engine validate source/include dependencies and reuse current caches.
        # Forcing all variants on every C#-only update can spend minutes optimizing an
        # unchanged shader. Keep a deliberate full-rebuild option for compiler debugging.
        $compileOptions = @('-j', '4')
        if ($ForceRecompileShaders) { $compileOptions += '-f' }
        & (Join-Path $enginePath 'bin/managed/ShaderCompiler.exe') @shaderFiles @compileOptions
        if ($LASTEXITCODE -ne 0) { throw 'Steam shader compilation failed.' }
    } finally {
        Pop-Location
    }
} catch {
    $deploymentError = $_
    $savedMount = Join-Path $backup 'mount'
    # Restoring old files alone leaves newly introduced DLL/shader sources in
    # place. Remove only payload files this invocation attempted to write and
    # which did not exist in the backup; never remove unrelated mount files.
    foreach ($target in $writtenTargets) {
        if (!$target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing rollback outside the mount directory: $target. Original failure: $deploymentError"
        }
        $savedFile = Join-Path $savedMount ([IO.Path]::GetRelativePath($destination, $target))
        if (!(Test-Path -LiteralPath $savedFile) -and (Test-Path -LiteralPath $target -PathType Leaf)) {
            Remove-Item -LiteralPath $target -Force
        }
    }
    if (Test-Path -LiteralPath $savedMount) {
        foreach ($savedFile in Get-ChildItem -LiteralPath $savedMount -File -Recurse) {
            $restoreTarget = Join-Path $destination ([IO.Path]::GetRelativePath($savedMount, $savedFile.FullName))
            New-Item -ItemType Directory -Path (Split-Path $restoreTarget -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $savedFile.FullName -Destination $restoreTarget -Force
        }
        throw "Deployment failed; previous files restored from $backup. $deploymentError"
    }
    throw "Deployment failed; newly written payload files removed (no previous mount existed). $deploymentError"
}
[ordered]@{
    deployedAt = (Get-Date).ToString('o')
    engineRoot = $enginePath
    engineSha256 = (Get-FileHash -LiteralPath (Join-Path $enginePath 'bin/managed/Sandbox.Engine.dll')).Hash
    backup = $backup
    files = @($manifest)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'deployment.json') -Encoding utf8
Write-Output "Deployed $($files.Count) files to $destination"
Write-Output "Previous mount backup: $backup"
Write-Output "DLL SHA256: $((Get-FileHash -LiteralPath (Join-Path $destination 'titanfall2.dll')).Hash)"
