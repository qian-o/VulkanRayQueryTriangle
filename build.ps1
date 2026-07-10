param(
    [switch]$Run
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $env:VULKAN_SDK) {
    throw 'VULKAN_SDK is not set.'
}

$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmakeCommand) {
    $cmake = $cmakeCommand.Source
} else {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw 'CMake was not found on PATH, and vswhere.exe is unavailable.'
    }

    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw 'CMake and Visual Studio C++ tools were not found.'
    }

    $cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (-not (Test-Path $cmake)) {
        throw "Visual Studio CMake was not found at $cmake."
    }
}

$source = $PSScriptRoot
$build = Join-Path $source 'build'

& $cmake -S $source -B $build -A x64
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$spirvVal = Join-Path $env:VULKAN_SDK 'Bin\spirv-val.exe'
if (-not (Test-Path $spirvVal)) {
    throw "spirv-val.exe was not found at $spirvVal."
}

& $spirvVal --target-env vulkan1.4 (Join-Path $source 'ray_query_heap.spv')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($Run) {
    $executable = Join-Path $build 'Release\vulkan_descriptor_heap_ray_query_repro.exe'
    & $executable
    exit $LASTEXITCODE
}
