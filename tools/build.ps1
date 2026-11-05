param(
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$publish = Join-Path $root 'artifacts\publish\portable'
$package = Join-Path $root 'artifacts\package'
$installer = Join-Path $root 'artifacts\installer'

Push-Location $root
try {
    & npm --prefix frontend run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    New-Item -ItemType Directory -Force $publish, $package, $installer | Out-Null
    & $dotnet publish src/QuanShuo.Native/QuanShuo.Native.csproj `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $publish `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Native publish failed.' }

    [xml]$versionProps = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    $version = [string]$versionProps.Project.PropertyGroup.Version
    $readme = (Get-Content (Join-Path $root 'docs\package-readme.md') -Raw -Encoding UTF8).Replace('{{VERSION}}', $version)
    [System.IO.File]::WriteAllText((Join-Path $package 'README.md'), $readme, [System.Text.UTF8Encoding]::new($false))
    $licenseNames = @('LICENSE', 'THIRD_PARTY_NOTICES.md')
    foreach ($name in $licenseNames) {
        Copy-Item (Join-Path $root $name) (Join-Path $package $name) -Force
    }
    $packageDocs = Join-Path $package 'docs'
    New-Item -ItemType Directory -Force $packageDocs | Out-Null
    $localPromptNames = @('local-asr-assessment-prompt.md', 'local-asr-agent-prompt.md')
    foreach ($name in $localPromptNames) {
        Copy-Item (Join-Path $root "docs\$name") (Join-Path $packageDocs $name) -Force
    }
    Compress-Archive -Path (Join-Path $publish 'QuanShuo.exe'), (Join-Path $package 'README.md') `
        -DestinationPath (Join-Path $package 'QuanShuo-portable.zip') -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open((Join-Path $package 'QuanShuo-portable.zip'), [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($name in $licenseNames) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $package $name), $name) | Out-Null
        }
        foreach ($name in $localPromptNames) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $packageDocs $name), "docs/$name") | Out-Null
        }
    } finally { $archive.Dispose() }

    if (-not $SkipInstaller) {
        & $dotnet publish src/QuanShuo.Installer/QuanShuo.Installer.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            --output $installer `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=None `
            -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
    }

    $releaseFiles = @((Join-Path $package 'QuanShuo-portable.zip'))
    if (-not $SkipInstaller) { $releaseFiles += Join-Path $installer 'QuanShuo-Setup.exe' }
    $checksums = $releaseFiles | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [System.IO.Path]::GetFileName($_) }
    [System.IO.File]::WriteAllLines((Join-Path $package 'SHA256SUMS.txt'), [string[]]$checksums, [System.Text.UTF8Encoding]::new($false))

    Write-Output "Portable exe: $publish\QuanShuo.exe"
    Write-Output "Portable ZIP: $package\QuanShuo-portable.zip"
    Write-Output "Setup exe: $installer\QuanShuo-Setup.exe"
    Write-Output "Checksums: $package\SHA256SUMS.txt"
} finally {
    Pop-Location
}
