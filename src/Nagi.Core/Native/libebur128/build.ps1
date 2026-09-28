param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'ARM64')]
    [string] $Architecture,

    [Parameter(Mandatory)]
    [string] $SourceRoot,

    [string] $BuildRoot = 'C:\NagiNativeBuild',
    [string] $VisualStudioRoot = 'C:\Program Files\Microsoft Visual Studio\18\Community'
)

$ErrorActionPreference = 'Stop'
$commit = (& git -C $SourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -ne '67b33abe1558160ed76ada1322329b0e9e058b02') {
    throw 'SourceRoot must be the unmodified libebur128 v1.2.6 commit 67b33abe1558160ed76ada1322329b0e9e058b02.'
}
if ((& git -C $SourceRoot status --porcelain).Count -ne 0) { throw 'SourceRoot has local changes.' }

$cmake = Join-Path $VisualStudioRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$toolsVersionFile = Join-Path $VisualStudioRoot 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt'
if (-not (Test-Path -LiteralPath $toolsVersionFile)) { throw "MSVC version file not found: $toolsVersionFile" }
$toolsVersion = (Get-Content -LiteralPath $toolsVersionFile -TotalCount 1).Trim()
$dumpbin = Join-Path $VisualStudioRoot "VC\Tools\MSVC\$toolsVersion\bin\Hostx64\x64\dumpbin.exe"
if (-not (Test-Path -LiteralPath $cmake)) { throw "CMake not found: $cmake" }
if (-not (Test-Path -LiteralPath $dumpbin)) { throw "dumpbin not found: $dumpbin" }

$build = Join-Path $BuildRoot $Architecture
$extraCFlags = if ($Architecture -eq 'ARM64') { @('-DCMAKE_C_FLAGS=/std:clatest') } else { @() }
& $cmake -S $SourceRoot -B $build -G 'Visual Studio 18 2026' -A $Architecture `
    '-DCMAKE_POLICY_VERSION_MINIMUM=3.5' '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW' `
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded' '-DBUILD_SHARED_LIBS=ON' '-DENABLE_TESTS=OFF' @extraCFlags
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed for $Architecture" }
& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw "Native build failed for $Architecture" }

$binary = Join-Path $build 'Release\ebur128.dll'
$headers = & $dumpbin /headers $binary
$expectedMachine = if ($Architecture -eq 'x64') { '8664 machine (x64)' } else { 'AA64 machine (ARM64)' }
if (-not ($headers | Select-String -SimpleMatch $expectedMachine)) { throw "Wrong PE machine for $Architecture" }
$imports = & $dumpbin /dependents $binary
if ($imports | Select-String 'VCRUNTIME|ucrtbase|MSVCP') { throw 'Unexpected dynamic C/C++ runtime import' }
$exports = & $dumpbin /exports $binary
foreach ($symbol in @('ebur128_init', 'ebur128_destroy', 'ebur128_add_frames_float', 'ebur128_loudness_global', 'ebur128_sample_peak')) {
    if (-not ($exports | Select-String -SimpleMatch $symbol)) { throw "Missing export: $symbol" }
}

$destination = Join-Path $PSScriptRoot (Join-Path $Architecture 'ebur128.dll')
New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
Copy-Item -LiteralPath $binary -Destination $destination -Force
Get-FileHash -LiteralPath $destination -Algorithm SHA256 | Select-Object Path, Hash
