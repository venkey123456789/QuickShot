param([string]$Destination = (Join-Path $PSScriptRoot '..\engines'), [string]$ArchiveCache)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'engines.lock.json') -Raw | ConvertFrom-Json
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('QuickShot-engines-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try {
    foreach ($package in $manifest.packages) {
        $archive = Join-Path $scratch ($package.name + '.zip')
        $cached = if ($ArchiveCache) { Join-Path $ArchiveCache ($package.name + '.zip') } else { $null }
        if ($cached -and (Test-Path -LiteralPath $cached)) { Copy-Item -LiteralPath $cached -Destination $archive }
        else { Invoke-WebRequest -Uri $package.url -OutFile $archive }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.sha256) {
            throw "Archive integrity check failed: $($package.name)"
        }
        $unpack = Join-Path $scratch $package.name
        Expand-Archive -LiteralPath $archive -DestinationPath $unpack
        foreach ($file in $package.files) {
            $source = [IO.Path]::GetFullPath((Join-Path $unpack $file.archivePath))
            $target = [IO.Path]::GetFullPath((Join-Path (Join-Path $destinationRoot $package.name) $file.path))
            if (-not $source.StartsWith($unpack + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                -not $target.StartsWith($destinationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Package path escaped its destination.'
            }
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { throw "File integrity check failed: $source" }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        Write-Output "Verified and installed $($package.name) $($package.version)"
    }
}
finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedScratch.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedScratch) -like 'QuickShot-engines-*') {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
