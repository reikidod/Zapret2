$ErrorActionPreference = "Stop"

$BundleCommit = "caf11b2d799325453e7eb9faa7217f771a268bfb"
$FlowsealCommit = "ffce9179b2b1f2aeb961f4d17274ada3b6d6bea5"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Work = Join-Path ([IO.Path]::GetTempPath()) ("zapret-runtime-" + [guid]::NewGuid().ToString("N"))
$BundleZip = Join-Path $Work "zapret-win-bundle.zip"
$FlowsealZip = Join-Path $Work "flowseal.zip"
$BundleExtract = Join-Path $Work "bundle"
$FlowsealExtract = Join-Path $Work "flowseal"
$ResourceDir = Join-Path $RepoRoot "src/Zapret2.App/Resources"
$ResourceZip = Join-Path $ResourceDir "zapret-runtime.zip"

try {
    New-Item -ItemType Directory -Force -Path $Work, $BundleExtract, $FlowsealExtract, $ResourceDir | Out-Null

    Write-Host "Downloading pinned Windows runtime bundle..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "https://github.com/bol-van/zapret-win-bundle/archive/$BundleCommit.zip" -OutFile $BundleZip
    Expand-Archive -LiteralPath $BundleZip -DestinationPath $BundleExtract -Force
    $BundleRoot = Get-ChildItem -LiteralPath $BundleExtract -Directory | Select-Object -First 1
    if (!$BundleRoot) { throw "The upstream Windows bundle archive is empty." }

    $required = @(
        "zapret-winws/winws.exe",
        "zapret-winws/winws2.exe",
        "zapret-winws/WinDivert.dll",
        "zapret-winws/WinDivert64.sys",
        "zapret-winws/cygwin1.dll",
        "zapret-winws/lua/zapret-lib.lua",
        "blockcheck/blockcheck.cmd",
        "blockcheck/blockcheck2.cmd",
        "cygwin/bin/bash.exe",
        "cygwin/bin/cygpath.exe",
        "tools/elevator.exe"
    )
    foreach ($relative in $required) {
        if (!(Test-Path (Join-Path $BundleRoot.FullName $relative))) {
            throw "Upstream bundle is missing required component: $relative"
        }
    }

    Write-Host "Downloading pinned Flowseal profiles and baseline lists..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "https://github.com/Flowseal/zapret-discord-youtube/archive/$FlowsealCommit.zip" -OutFile $FlowsealZip
    Expand-Archive -LiteralPath $FlowsealZip -DestinationPath $FlowsealExtract -Force
    $FlowsealRoot = Get-ChildItem -LiteralPath $FlowsealExtract -Directory | Select-Object -First 1
    if (!$FlowsealRoot) { throw "The pinned Flowseal archive is empty." }

    $StrategyDir = Join-Path $BundleRoot.FullName "flowseal-strategies"
    New-Item -ItemType Directory -Force -Path $StrategyDir | Out-Null
    $strategyFiles = @(
        "general.bat", "general (ALT).bat",
        "general (ALT2).bat", "general (ALT3).bat", "general (ALT4).bat",
        "general (ALT5).bat", "general (ALT6).bat", "general (ALT7).bat",
        "general (ALT8).bat", "general (ALT9).bat", "general (ALT10).bat",
        "general (ALT11).bat", "general (ALT12).bat", "general (ALT13).bat",
        "general (EXP).bat", "general (FAKE TLS AUTO).bat",
        "general (FAKE TLS AUTO ALT).bat", "general (FAKE TLS AUTO ALT2).bat",
        "general (FAKE TLS AUTO ALT3).bat", "general (SIMPLE FAKE).bat",
        "general (SIMPLE FAKE ALT).bat", "general (SIMPLE FAKE ALT2).bat"
    )
    foreach ($file in $strategyFiles) {
        $source = Join-Path $FlowsealRoot.FullName $file
        if (!(Test-Path $source)) { throw "Pinned Flowseal profile is missing: $file" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $StrategyDir $file) -Force
    }

    $sourceLists = Join-Path $FlowsealRoot.FullName "lists"
    if (Test-Path $sourceLists) {
        $targetLists = Join-Path $BundleRoot.FullName "lists"
        New-Item -ItemType Directory -Force -Path $targetLists | Out-Null
        Get-ChildItem -LiteralPath $sourceLists -File | Copy-Item -Destination $targetLists -Force
    }

    $manifest = [ordered]@{
        bundle_repository = "bol-van/zapret-win-bundle"
        bundle_commit = $BundleCommit
        flowseal_repository = "Flowseal/zapret-discord-youtube"
        flowseal_commit = $FlowsealCommit
        includes = @("winws.exe", "winws2.exe", "blockcheck.cmd", "blockcheck2.cmd", "WinDivert", "Cygwin", "Lua", "Flowseal profiles", "Flowseal lists")
    } | ConvertTo-Json -Depth 4
    Set-Content -LiteralPath (Join-Path $BundleRoot.FullName "zapret2-bundle-manifest.json") -Value $manifest -Encoding UTF8

    if (Test-Path $ResourceZip) { Remove-Item -LiteralPath $ResourceZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $BundleRoot.FullName, $ResourceZip,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $size = (Get-Item -LiteralPath $ResourceZip).Length
    if ($size -lt 1MB) { throw "The embedded runtime archive is unexpectedly small ($size bytes)." }
    $hash = (Get-FileHash -LiteralPath $ResourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
    @(
        "Runtime bundle archive: $size bytes",
        "Runtime bundle SHA-256: $hash",
        "Zapret bundle commit: $BundleCommit",
        "Flowseal commit: $FlowsealCommit"
    ) | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append
    Write-Host "Embedded runtime archive prepared: $ResourceZip ($size bytes)" -ForegroundColor Green
}
finally {
    if (Test-Path $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
}
