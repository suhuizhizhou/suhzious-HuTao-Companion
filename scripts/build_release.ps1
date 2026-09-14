param(
    [string]$Version = '0.1.0',
    [string]$Runtime = 'win-x64',
    [switch]$WithoutVoiceRuntime,
    [int]$ArchivePartSizeMiB = 1900
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'release'))
$packageName = "HuTao-Companion-v$Version-$Runtime"
$packageDirectory = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot $packageName))
$legacyZipPath = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot "$packageName.zip"))
$fullArchivePath = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot "$packageName-full.tar.gz"))
$releaseNotesPath = Join-Path $repoRoot "docs/release-v$Version.md"
$releasePrefix = $releaseRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Assert-ReleasePath([string]$Path) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($script:releasePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝处理 release 目录以外的路径：$resolved"
    }
}

function Invoke-RobocopyDirectory(
    [string]$Source,
    [string]$Destination,
    [string[]]$ExcludedDirectories = @(),
    [string[]]$ExcludedFiles = @()
) {
    if (-not (Test-Path -LiteralPath $Source)) {
        throw "缺少要打包的目录：$Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $arguments = @($Source, $Destination, '/E', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($ExcludedDirectories.Count -gt 0) {
        $arguments += '/XD'
        $arguments += $ExcludedDirectories
    }
    if ($ExcludedFiles.Count -gt 0) {
        $arguments += '/XF'
        $arguments += $ExcludedFiles
    }
    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "复制目录失败（robocopy=$LASTEXITCODE）：$Source"
    }
}

function Find-CondaPack {
    if (-not [string]::IsNullOrWhiteSpace($env:HU_TAO_CONDA_PACK) -and
        (Test-Path -LiteralPath $env:HU_TAO_CONDA_PACK)) {
        return [pscustomobject]@{
            Executable = [System.IO.Path]::GetFullPath($env:HU_TAO_CONDA_PACK)
            Script = $null
        }
    }

    foreach ($root in @(
        'C:\ProgramData\anaconda3',
        'C:\ProgramData\miniconda3',
        (Join-Path $env:USERPROFILE 'anaconda3'),
        (Join-Path $env:USERPROFILE 'miniconda3')
    )) {
        $python = Join-Path $root 'python.exe'
        $script = Join-Path $root 'Scripts\conda-pack-script.py'
        if ((Test-Path -LiteralPath $python) -and (Test-Path -LiteralPath $script)) {
            return [pscustomobject]@{
                Executable = $python
                Script = $script
            }
        }
    }

    $command = Get-Command conda-pack -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return [pscustomobject]@{
            Executable = $command.Source
            Script = $null
        }
    }

    throw @"
找不到 conda-pack。完整语音 Release 需要将 PyTorch 环境转换为便携环境。
请先执行：conda install -n base -c conda-forge conda-pack
也可设置 HU_TAO_CONDA_PACK 指向 conda-pack.exe。
"@
}

function Copy-ConfiguredVoiceAssets([string]$DestinationRoot) {
    $dataVoiceRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'data/voice'))
    $allowedPrefix = $dataVoiceRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $selectedByCharacter = @{}

    foreach ($characterId in @('hutao', 'furina', 'klee')) {
        $selectedByCharacter[$characterId] = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        $catalogPath = Join-Path $repoRoot "data/persona/$characterId/emotion-references.json"
        if (-not (Test-Path -LiteralPath $catalogPath)) {
            throw "缺少情绪参考音频配置：$catalogPath"
        }
        $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
        foreach ($emotion in $catalog.emotions.PSObject.Properties.Value) {
            foreach ($reference in $emotion.references) {
                $source = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $catalogPath -Parent) $reference.audio))
                if (-not $source.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "参考音频越出 data/voice：$source"
                }
                if (-not (Test-Path -LiteralPath $source)) {
                    throw "缺少参考音频：$source"
                }
                [void]$selectedByCharacter[$characterId].Add($source)
            }
        }
    }

    $greetings = [ordered]@{
        hutao = 'data/voice/hutao/wav/7dda0f32efe522a2.wav'
        furina = 'data/voice/furina/wav/b25b2cc32be97733.wav'
        klee = 'data/voice/klee/wav/793e68bd5fa341c4.wav'
    }
    foreach ($entry in $greetings.GetEnumerator()) {
        $source = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $entry.Value))
        if (-not (Test-Path -LiteralPath $source)) {
            throw "缺少角色开场音频：$source"
        }
        [void]$selectedByCharacter[$entry.Key].Add($source)
    }

    $repoPrefix = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($characterId in $selectedByCharacter.Keys) {
        foreach ($source in $selectedByCharacter[$characterId]) {
            $relative = $source.Substring($repoPrefix.Length)
            $destination = Join-Path $DestinationRoot $relative
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }

        # 原声检索清单只保留随包提供的音频，避免运行时返回不存在的 WAV。
        $sourceManifest = Join-Path $repoRoot "data/voice/$characterId/manifest.jsonl"
        $destinationManifest = Join-Path $DestinationRoot "data/voice/$characterId/manifest.jsonl"
        $manifestLines = New-Object 'System.Collections.Generic.List[string]'
        if (Test-Path -LiteralPath $sourceManifest) {
            foreach ($line in [System.IO.File]::ReadLines($sourceManifest)) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                $record = $line | ConvertFrom-Json
                $sourceAudio = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $sourceManifest -Parent) $record.audio))
                if ($selectedByCharacter[$characterId].Contains($sourceAudio)) {
                    $manifestLines.Add($line)
                }
            }
        }
        New-Item -ItemType Directory -Path (Split-Path $destinationManifest -Parent) -Force | Out-Null
        [System.IO.File]::WriteAllLines($destinationManifest, $manifestLines, [System.Text.UTF8Encoding]::new($false))
        Write-Host "[$characterId] 已打包 $($selectedByCharacter[$characterId].Count) 条参考/开场音频"
    }
}

function Add-PortableVoiceRuntime([string]$DestinationRoot) {
    $developerPython = Join-Path $repoRoot 'voice/.venv/Scripts/python.exe'
    $developerPackages = Join-Path $repoRoot 'voice/.venv/Lib/site-packages'
    $gptSovitsSource = Join-Path $repoRoot 'voice/GPT-SoVITS-main'
    foreach ($required in @($developerPython, $developerPackages, $gptSovitsSource)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "完整语音 Release 缺少本地资源：$required"
        }
    }

    $basePrefix = (& $developerPython -c 'import sys; print(sys.base_prefix)').Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $basePrefix)) {
        throw "无法从 voice/.venv 定位基础 Python 环境：$basePrefix"
    }

    $condaPack = Find-CondaPack
    $portableTar = Join-Path $releaseRoot ".portable-python-$PID.tar"
    Assert-ReleasePath $portableTar
    if (Test-Path -LiteralPath $portableTar) {
        Remove-Item -LiteralPath $portableTar -Force
    }

    Write-Host "正在打包便携 Python/PyTorch：$basePrefix"
    $condaPackArguments = @()
    if (-not [string]::IsNullOrWhiteSpace($condaPack.Script)) {
        $condaPackArguments += $condaPack.Script
    }
    $condaPackArguments += @(
        '--prefix', $basePrefix,
        '--output', $portableTar,
        '--format', 'tar',
        '--force',
        '--ignore-editable-packages',
        '--ignore-missing-files'
    )
    & $condaPack.Executable @condaPackArguments
    if ($LASTEXITCODE -ne 0) {
        throw "conda-pack 失败，退出码：$LASTEXITCODE"
    }

    $portablePython = Join-Path $DestinationRoot 'voice/python'
    New-Item -ItemType Directory -Path $portablePython -Force | Out-Null
    & tar.exe -xf $portableTar -C $portablePython
    if ($LASTEXITCODE -ne 0) {
        throw "解压便携 Python 失败，退出码：$LASTEXITCODE"
    }
    Remove-Item -LiteralPath $portableTar -Force

    Write-Host '正在复制 GPT-SoVITS Python 依赖…'
    Invoke-RobocopyDirectory `
        $developerPackages `
        (Join-Path $DestinationRoot 'voice/python-packages') `
        @('__pycache__') `
        @('*.pyc', '*.pyo')

    Write-Host '正在复制 GPT-SoVITS v3 本体、模型权重与 FFmpeg…'
    Invoke-RobocopyDirectory `
        $gptSovitsSource `
        (Join-Path $DestinationRoot 'voice/GPT-SoVITS-main') `
        @('.git', '__pycache__', 'TEMP', 'logs', 'logs_v1', 'logs_v2', 'logs_v3', 'logs_v4') `
        @('*.pyc', '*.pyo', '*.log')

    Copy-ConfiguredVoiceAssets $DestinationRoot

    # 完整 Release 同时携带角色原声检索库与剧情 RAG 数据，使用者无需另行准备运行资源。
    foreach ($characterId in @('hutao', 'furina', 'klee')) {
        Invoke-RobocopyDirectory `
            (Join-Path $repoRoot "data/voice/$characterId") `
            (Join-Path $DestinationRoot "data/voice/$characterId") `
            @('__pycache__') `
            @('*.pyc', '*.pyo')
    }
    Invoke-RobocopyDirectory `
        (Join-Path $repoRoot 'data/story') `
        (Join-Path $DestinationRoot 'data/story') `
        @('__pycache__') `
        @('*.pyc', '*.pyo')

    foreach ($required in @(
        'voice/python/python.exe',
        'voice/python/Scripts/conda-unpack-script.py',
        'voice/python-packages/fastapi/__init__.py',
        'voice/GPT-SoVITS-main/GPT_SoVITS/pretrained_models/s1v3.ckpt',
        'voice/GPT-SoVITS-main/GPT_SoVITS/pretrained_models/s2Gv3.pth',
        'voice/GPT-SoVITS-main/ffmpeg.exe'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $DestinationRoot $required))) {
            throw "语音 Release 缺少文件：$required"
        }
    }
}

function Split-LargeArchive(
    [string]$ArchivePath,
    [int64]$PartSizeBytes
) {
    $buffer = New-Object byte[] (8MB)
    $input = [System.IO.File]::OpenRead($ArchivePath)
    $partPaths = New-Object 'System.Collections.Generic.List[string]'
    try {
        $partNumber = 1
        while ($input.Position -lt $input.Length) {
            $partPath = '{0}.{1:D3}' -f $ArchivePath, $partNumber
            $output = [System.IO.File]::Create($partPath)
            try {
                $written = 0L
                while ($written -lt $PartSizeBytes) {
                    $toRead = [int][Math]::Min($buffer.Length, $PartSizeBytes - $written)
                    $read = $input.Read($buffer, 0, $toRead)
                    if ($read -eq 0) { break }
                    $output.Write($buffer, 0, $read)
                    $written += $read
                }
            }
            finally {
                $output.Dispose()
            }
            $partPaths.Add($partPath)
            $partNumber++
        }
    }
    finally {
        $input.Dispose()
    }
    return $partPaths
}

Assert-ReleasePath $packageDirectory
if (-not (Test-Path -LiteralPath $releaseNotesPath)) {
    throw "缺少当前版本的 Release Notes：$releaseNotesPath"
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
foreach ($oldOutput in @($legacyZipPath, "$legacyZipPath.sha256", $fullArchivePath)) {
    if (Test-Path -LiteralPath $oldOutput) {
        Remove-Item -LiteralPath $oldOutput -Force
    }
}
foreach ($oldOutput in @(
    (Join-Path $releaseRoot "$packageName-SHA256SUMS.txt"),
    (Join-Path $releaseRoot 'assemble-release.ps1')
)) {
    if (Test-Path -LiteralPath $oldOutput) {
        Remove-Item -LiteralPath $oldOutput -Force
    }
}
Get-ChildItem -LiteralPath $releaseRoot -Filter "$packageName-full.tar.gz.*" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'agent/.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'agent/.nuget'
$env:MSBuildEnableWorkloadResolver = 'false'

$project = Join-Path $repoRoot 'hosts/HuTao.Pet/HuTao.Pet.csproj'
dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $packageDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码：$LASTEXITCODE"
}

New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'data') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'voice') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'data/persona') `
    -Destination (Join-Path $packageDirectory 'data/persona') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'voice/infer') `
    -Destination (Join-Path $packageDirectory 'voice/infer') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'voice/requirements.txt') `
    -Destination (Join-Path $packageDirectory 'voice/requirements.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot '.env.example') `
    -Destination (Join-Path $packageDirectory '.env.example')
Copy-Item -LiteralPath (Join-Path $repoRoot 'release-assets/README.md') `
    -Destination (Join-Path $packageDirectory 'README.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'release-assets/run.bat') `
    -Destination (Join-Path $packageDirectory 'run.bat')
Copy-Item -LiteralPath (Join-Path $repoRoot 'release-assets/resource-check.ps1') `
    -Destination (Join-Path $packageDirectory 'resource-check.ps1')
Copy-Item -LiteralPath $releaseNotesPath `
    -Destination (Join-Path $packageDirectory 'RELEASE_NOTES.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/release-resources.md') `
    -Destination (Join-Path $packageDirectory 'RESOURCE_MANIFEST.md')

foreach ($runtimeDirectory in @('data/voice/generated', 'data/reading')) {
    New-Item -ItemType Directory -Path (Join-Path $packageDirectory $runtimeDirectory) -Force | Out-Null
}

if (-not $WithoutVoiceRuntime) {
    Add-PortableVoiceRuntime $packageDirectory
}

if ($WithoutVoiceRuntime) {
    Compress-Archive -LiteralPath $packageDirectory -DestinationPath $legacyZipPath -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $legacyZipPath -Algorithm SHA256
    [System.IO.File]::WriteAllText(
        "$legacyZipPath.sha256",
        "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($legacyZipPath))$([Environment]::NewLine)")
    Write-Host "轻量 Release 压缩包：$legacyZipPath"
    Write-Host "SHA256：$($hash.Hash)"
}
else {
    Write-Host '正在压缩完整语音 Release（耗时取决于磁盘与 CPU）…'
    Push-Location $releaseRoot
    try {
        & tar.exe -czf $fullArchivePath $packageName
        if ($LASTEXITCODE -ne 0) {
            throw "完整 Release 压缩失败，退出码：$LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    $partSizeBytes = [int64]$ArchivePartSizeMiB * 1MB
    $releaseAssets = New-Object 'System.Collections.Generic.List[string]'
    if ((Get-Item -LiteralPath $fullArchivePath).Length -gt $partSizeBytes) {
        foreach ($part in (Split-LargeArchive $fullArchivePath $partSizeBytes)) {
            $releaseAssets.Add($part)
        }
        Remove-Item -LiteralPath $fullArchivePath -Force
        Copy-Item -LiteralPath (Join-Path $repoRoot 'release-assets/assemble-release.ps1') `
            -Destination (Join-Path $releaseRoot 'assemble-release.ps1') -Force
        $releaseAssets.Add((Join-Path $releaseRoot 'assemble-release.ps1'))
    }
    else {
        $releaseAssets.Add($fullArchivePath)
    }

    $checksums = New-Object 'System.Collections.Generic.List[string]'
    foreach ($asset in $releaseAssets) {
        $hash = Get-FileHash -LiteralPath $asset -Algorithm SHA256
        $checksums.Add("$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($asset))")
    }
    $checksumPath = Join-Path $releaseRoot "$packageName-SHA256SUMS.txt"
    [System.IO.File]::WriteAllLines($checksumPath, $checksums, [System.Text.UTF8Encoding]::new($false))
    Write-Host "完整 Release 共 $($releaseAssets.Count) 个发布资产，校验表：$checksumPath"
}

Write-Host "Release 目录：$packageDirectory"
