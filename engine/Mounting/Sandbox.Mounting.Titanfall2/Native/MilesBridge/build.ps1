param(
	[string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\..\..\game\mount\titanfall2')
)

$ErrorActionPreference = 'Stop'
$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ( [string]::IsNullOrWhiteSpace( $vsRoot ) ) { throw 'Visual Studio C++ build tools are required.' }

$devCmd = Join-Path $vsRoot 'Common7\Tools\VsDevCmd.bat'
$source = Join-Path $PSScriptRoot 'miles_bridge.cpp'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory 'titanfall2_miles.dll'
$command = "`"$devCmd`" -arch=x64 -host_arch=x64 && cl /nologo /std:c++17 /EHsc /LD /O2 `"$source`" /link /OUT:`"$output`""
cmd.exe /d /s /c $command
if ( $LASTEXITCODE -ne 0 ) { throw "Miles bridge build failed with exit code $LASTEXITCODE." }
