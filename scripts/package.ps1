param(
    [string]$Configuration = 'Release',
    [string]$Version = '0.3.0'
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path "$PSScriptRoot\..").Path
$dotnet = Join-Path $env:LOCALAPPDATA 'NetScopeTools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$publish = Join-Path $repository 'artifacts\publish\win-x64'
$zip = Join-Path $repository "artifacts\NetScope-$Version-win-x64-portable-unsigned.zip"
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$publishFull = [System.IO.Path]::GetFullPath($publish)
if (-not $publishFull.StartsWith($artifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish directory escaped artifacts root.'
}

& $dotnet test (Join-Path $repository 'tests\NetScope.Tests\NetScope.Tests.csproj') -c $Configuration --no-restore -m:1
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

if (Test-Path -LiteralPath $publishFull) { Remove-Item -LiteralPath $publishFull -Recurse -Force }
& $dotnet publish (Join-Path $repository 'src\NetScope.App\NetScope.App.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -m:1 -o $publish `
    -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# 后台 Collector 与 App 同目录发布，CollectorLauncher 按 AppContext.BaseDirectory 发现 NetScope.Collector.exe
& $dotnet publish (Join-Path $repository 'src\NetScope.Collector\NetScope.Collector.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -m:1 -o $publish `
    -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Collector publish failed.' }
$collectorExe = Join-Path $publish 'NetScope.Collector.exe'
if (-not (Test-Path -LiteralPath $collectorExe)) { throw 'Collector executable missing from publish output.' }

$marker = Join-Path $publish 'UNSIGNED-DEVELOPMENT-BUILD.txt'
[System.IO.File]::WriteAllText($marker, "NetScope $Version development build`r`nThis build is not Authenticode signed.`r`n")
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if ($iscc) {
    & $iscc (Join-Path $repository 'installer\NetScope.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
} else {
    Write-Warning 'Inno Setup 6 is not installed; portable package is ready and installer script was generated.'
}

Get-ChildItem -LiteralPath (Join-Path $repository 'artifacts') -File | Select-Object Name, Length, LastWriteTime
