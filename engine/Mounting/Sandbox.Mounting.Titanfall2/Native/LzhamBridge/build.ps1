param(
	[string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath( (Join-Path $PSScriptRoot '..\..\..\..\..') )
if ( [string]::IsNullOrWhiteSpace( $OutputDirectory ) )
{
	$OutputDirectory = Join-Path $repositoryRoot 'game\mount\titanfall2'
}

$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ( [string]::IsNullOrWhiteSpace( $vsRoot ) ) { throw 'Visual Studio C++ build tools are required.' }

$devCmd = Join-Path $vsRoot 'Common7\Tools\VsDevCmd.bat'
$source = Join-Path $PSScriptRoot 'lzham_bridge.cpp'
$lzhamRoot = Join-Path $PSScriptRoot 'ThirdParty\lzham-alpha8'
$library = Join-Path $lzhamRoot 'lib\liblzham_x64.lib'
$include = Join-Path $lzhamRoot 'include'
if ( !(Test-Path -LiteralPath $library) -or !(Test-Path -LiteralPath $include) ) { throw "Vendored LZHAM files are missing: $lzhamRoot" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory 'titanfall2_lzham.dll'
$command = "`"$devCmd`" -arch=x64 -host_arch=x64 && cl /nologo /std:c++17 /LD /O2 /I `"$include`" `"$source`" /link /OUT:`"$output`" `"$library`""
cmd.exe /d /s /c $command
if ( $LASTEXITCODE -ne 0 ) { throw "LZHAM bridge build failed with exit code $LASTEXITCODE." }
