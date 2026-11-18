# Builds the OmniWin app (fork of Handy) on this PC without changing any system setting: the tool paths are added for this run only.
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File build-omniwin-app.ps1 [-Debug]
param([switch]$Debug)
$ErrorActionPreference = 'Stop'

$bun = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Oven-sh.Bun_Microsoft.Winget.Source_8wekyb3d8bbwe\bun-windows-x64'
$extra = @($bun, (Join-Path $env:USERPROFILE '.cargo\bin'), 'C:\Program Files\CMake\bin')
$env:PATH = ($extra -join ';') + ';' + $env:PATH
if (-not $env:VULKAN_SDK) { $env:VULKAN_SDK = [Environment]::GetEnvironmentVariable('VULKAN_SDK', 'Machine') }
if (-not $env:VULKAN_SDK) { $env:VULKAN_SDK = [Environment]::GetEnvironmentVariable('VULKAN_SDK', 'User') }
$env:PATH = (Join-Path $env:VULKAN_SDK 'Bin') + ';' + $env:PATH

Set-Location $PSScriptRoot
$args = @('tauri', 'build', '--no-bundle')
if ($Debug) { $args += '--debug' }
& bun @args
exit $LASTEXITCODE
