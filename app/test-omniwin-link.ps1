# Runs the Rust tests of the OmniWin link against the published server (read-only calls). Tool paths are set for this run only.
$ErrorActionPreference = 'Stop'
$env:PATH = (Join-Path $env:USERPROFILE '.cargo\bin') + ';C:\Program Files\CMake\bin;' + $env:PATH
if (-not $env:VULKAN_SDK) { $env:VULKAN_SDK = [Environment]::GetEnvironmentVariable('VULKAN_SDK', 'Machine') }
$env:PATH = (Join-Path $env:VULKAN_SDK 'Bin') + ';' + $env:PATH
$env:OMNIWIN_SERVER = (Resolve-Path (Join-Path $PSScriptRoot '..\publish\OmniWin.Server.exe')).Path
$env:PATH = (Join-Path $PSScriptRoot 'src-tauri\transcribe-libs') + ';' + $env:PATH   # the speech-engine DLLs the test program loads
Set-Location (Join-Path $PSScriptRoot 'src-tauri')
& cargo test --release --lib omniwin:: -- --test-threads=1
exit $LASTEXITCODE
