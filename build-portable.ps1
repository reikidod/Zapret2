$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectRoot

Write-Host "Preparing embedded winws/winws2 and blockcheck runtime..." -ForegroundColor Cyan
.\scripts\Prepare-Bundle.ps1

Write-Host "Restoring solution..." -ForegroundColor Cyan
dotnet restore .\Zapret2.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test .\Zapret2.sln --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Tests failed; portable build was not created." }

Write-Host "Publishing self-contained single-file Windows x64 application..." -ForegroundColor Cyan
dotnet publish .\src\Zapret2.App\Zapret2.App.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output .\publish\win-x64

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
$exe = Join-Path $projectRoot "publish\win-x64\Zapret2ControlCenter.exe"
if (!(Test-Path $exe)) { throw "Expected executable not found: $exe" }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Zapret2ControlCenter.exe" | Set-Content (Join-Path (Split-Path $exe) "SHA256SUMS.txt") -Encoding ASCII
@'
Zapret2 Control Center — Windows x64 portable build

Run Zapret2ControlCenter.exe. No separately installed .NET Runtime is required.
The EXE includes winws.exe, winws2.exe, both blockcheck scripts, WinDivert, Cygwin, Lua, Flowseal profiles and baseline lists.
User configuration is stored in %APPDATA%\Zapret2GUI and is not inside the EXE.
'@ | Set-Content (Join-Path (Split-Path $exe) "README.txt") -Encoding UTF8
Write-Host "Portable EXE created: $exe" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor DarkGray
