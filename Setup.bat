@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1

rem Keep setup's temporary files and HTTP cache writable even when the user's Temp is restricted.
rem setlocal limits these overrides to setup and its child processes.
set "SBOX_SETUP_CACHE=%~dp0.intermediate\setup"
set "TEMP=%SBOX_SETUP_CACHE%\temp"
set "TMP=%TEMP%"
set "NUGET_HTTP_CACHE_PATH=%SBOX_SETUP_CACHE%\nuget-http"
if not exist "%TEMP%\" mkdir "%TEMP%"
if not exist "%TEMP%\" goto cache_error
if not exist "%NUGET_HTTP_CACHE_PATH%\" mkdir "%NUGET_HTTP_CACHE_PATH%"
if not exist "%NUGET_HTTP_CACHE_PATH%\" goto cache_error

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET 10 SDK is required but 'dotnet' was not found on PATH.
    echo Install it from https://dotnet.microsoft.com/download and rerun Setup.bat.
    popd
    exit /b 1
)

dotnet run --verbosity quiet --project ".\engine\Tools\SboxBuild\SboxBuild.csproj" -- bootstrap %*
popd & exit /b %errorlevel%

:cache_error
echo Could not create setup's local cache directories under "%SBOX_SETUP_CACHE%".
popd
exit /b 1
