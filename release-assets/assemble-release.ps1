param(
    [string]$ArchiveBaseName = '',
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$assetRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ArchiveBaseName)) {
    $firstPart = Get-ChildItem -LiteralPath $assetRoot -Filter 'HuTao-Companion-*-full.tar.gz.001' -File |
        Select-Object -First 1
    if ($null -eq $firstPart) {
        throw '没有找到 HuTao-Companion-*-full.tar.gz.001。'
    }
    $ArchiveBaseName = $firstPart.Name.Substring(0, $firstPart.Name.Length - 4)
}
$parts = @(Get-ChildItem -LiteralPath $assetRoot -Filter "$ArchiveBaseName.*" -File |
    Where-Object { $_.Extension -match '^\.\d{3}$' } |
    Sort-Object Name)
if ($parts.Count -eq 0) {
    throw "没有找到分卷：$ArchiveBaseName.001"
}

for ($index = 0; $index -lt $parts.Count; $index++) {
    $expected = '{0}.{1:D3}' -f $ArchiveBaseName, ($index + 1)
    if ($parts[$index].Name -ne $expected) {
        throw "分卷不连续，缺少：$expected"
    }
}

$checksumFile = Get-ChildItem -LiteralPath $assetRoot -Filter 'HuTao-Companion-*-SHA256SUMS.txt' -File |
    Select-Object -First 1
if ($null -ne $checksumFile) {
    $expectedHashes = @{}
    foreach ($line in [System.IO.File]::ReadLines($checksumFile.FullName)) {
        if ($line -match '^([0-9a-fA-F]{64})\s+(.+)$') {
            $expectedHashes[$Matches[2]] = $Matches[1]
        }
    }
    foreach ($part in $parts) {
        if (-not $expectedHashes.ContainsKey($part.Name)) {
            throw "校验表中缺少分卷：$($part.Name)"
        }
        Write-Host "校验 $($part.Name)…"
        $actual = (Get-FileHash -LiteralPath $part.FullName -Algorithm SHA256).Hash
        if (-not $actual.Equals($expectedHashes[$part.Name], [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "分卷损坏或下载不完整：$($part.Name)"
        }
    }
}
elseif ($VerifyOnly) {
    throw '校验模式需要同目录中的 *-SHA256SUMS.txt。'
}

if ($VerifyOnly) {
    Write-Host "全部 $($parts.Count) 个分卷校验通过。"
    return
}

$archivePath = Join-Path $assetRoot $ArchiveBaseName
$output = [System.IO.File]::Create($archivePath)
try {
    foreach ($part in $parts) {
        Write-Host "合并 $($part.Name)…"
        $input = [System.IO.File]::OpenRead($part.FullName)
        try { $input.CopyTo($output) }
        finally { $input.Dispose() }
    }
}
finally {
    $output.Dispose()
}

Write-Host '正在解压完整 Release…'
& tar.exe -xzf $archivePath -C $assetRoot
if ($LASTEXITCODE -ne 0) {
    throw "解压失败，退出码：$LASTEXITCODE"
}
Write-Host "完成。请进入解压后的 HuTao-Companion 目录并运行 run.bat。"
