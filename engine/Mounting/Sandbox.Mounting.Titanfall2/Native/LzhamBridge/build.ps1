param(
	[string]$LzhamRoot = 'D:\ttf2 sbox\ttf2-sbox-main\3rd\TFVPKTool-main\src\lzham',
	[string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\..\..\game\mount\titanfall2')
)

$ErrorActionPreference = 'Stop'
$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ( [string]::IsNullOrWhiteSpace( $vsRoot ) ) { throw 'Visual Studio C++ build tools are required.' }

$devCmd = Join-Path $vsRoot 'Common7\Tools\VsDevCmd.bat'
$source = Join-Path $PSScriptRoot 'lzham_bridge.cpp'
$library = Join-Path $LzhamRoot 'lib\liblzham_x64.lib'
$include = Join-Path $LzhamRoot 'include'
if ( !(Test-Path -LiteralPath $library) -or !(Test-Path -LiteralPath $include) ) { throw "LZHAM source root is invalid: $LzhamRoot" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory 'titanfall2_lzham.dll'
$command = "`"$devCmd`" -arch=x64 -host_arch=x64 && cl /nologo /std:c++17 /LD /O2 /I `"$include`" `"$source`" /link /OUT:`"$output`" `"$library`""
cmd.exe /d /s /c $command
if ( $LASTEXITCODE -ne 0 ) { throw "LZHAM bridge build failed with exit code $LASTEXITCODE." }
