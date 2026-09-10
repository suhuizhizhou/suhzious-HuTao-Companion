$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot

$required = @(
    'HuTao.Pet.exe',
    'data/persona/hutao/persona.yaml',
    'data/persona/furina/persona.yaml',
    'data/persona/klee/persona.yaml'
)
$voiceRequired = @(
    'voice/python/python.exe',
    'voice/python/Scripts/conda-unpack-script.py',
    'voice/python-packages/fastapi/__init__.py',
    'voice/GPT-SoVITS-main/GPT_SoVITS/pretrained_models/s1v3.ckpt',
    'voice/GPT-SoVITS-main/GPT_SoVITS/pretrained_models/s2Gv3.pth',
    'voice/GPT-SoVITS-main/ffmpeg.exe',
    'data/voice/hutao/manifest.jsonl',
    'data/voice/furina/manifest.jsonl',
    'data/voice/klee/manifest.jsonl',
    'data/story/index.json',
    'data/story/dialogue'
)
$hasVoiceRuntime = Test-Path -LiteralPath (Join-Path $packageRoot 'voice/python/python.exe')
if ($hasVoiceRuntime) {
    $required += $voiceRequired
}

$optional = [ordered]@{
    'DeepSeek API 配置（需使用者填写）' = '.env'
}

$missingRequired = @()
Write-Host '基础运行文件：'
foreach ($relativePath in $required) {
    $exists = Test-Path -LiteralPath (Join-Path $packageRoot $relativePath)
    Write-Host ('  [{0}] {1}' -f $(if ($exists) { 'OK' } else { '缺失' }), $relativePath)
    if (-not $exists) { $missingRequired += $relativePath }
}

Write-Host ''
Write-Host '外部配置：'
foreach ($entry in $optional.GetEnumerator()) {
    $exists = Test-Path -LiteralPath (Join-Path $packageRoot $entry.Value)
    Write-Host ('  [{0}] {1}：{2}' -f $(if ($exists) { '就绪' } else { '未配置' }), $entry.Key, $entry.Value)
}

if ($missingRequired.Count -gt 0) {
    Write-Error 'Release 基础文件不完整，请重新执行 scripts/build_release.ps1。'
    exit 1
}

Write-Host ''
if ($hasVoiceRuntime) {
    Write-Host '完整语音运行资源齐备。未填写 .env 时使用离线 Mock；填写 DEEPSEEK_API_KEY 后启用真实对话。'
}
else {
    Write-Host '这是不含语音模型与剧情库的轻量版；基础文字模式可运行。'
}
