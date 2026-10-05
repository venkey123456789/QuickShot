param([string]$ArchiveCache)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = Join-Path $root 'bin\Release\net9.0-windows\win-x64\publish'
dotnet publish (Join-Path $root 'QuickShot.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugSymbols=false -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$dist = Join-Path $root 'dist'
[IO.Directory]::CreateDirectory($dist) | Out-Null
$stage = Join-Path $dist ('stage-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'QuickShot.exe') -Destination $stage
foreach ($name in @('README.md','LICENSE','THIRD-PARTY-NOTICES.txt','VERIFICATION.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $stage -Recurse
Copy-Item -LiteralPath $PSScriptRoot -Destination (Join-Path $stage 'scripts') -Recurse
& (Join-Path $PSScriptRoot 'Install-Engines.ps1') -Destination (Join-Path $stage 'engines') -ArchiveCache $ArchiveCache
$zip = Join-Path $dist 'QuickShot-win-x64.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), "$digest  QuickShot-win-x64.zip`n")
Write-Output "Windows package: $zip"
Write-Output "Stage: $stage"
Write-Output "SHA256: $digest"
