# 一键运行 Agent 控制台演示（无 GUI）
#
# 模块划分见 docs/modules.md。宿主 exe 在 hosts/HuTao.Host/ 下。

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'hosts\HuTao.Host\bin\Debug\net9.0\HuTao.Agent.Host.exe'

if (-not (Test-Path $exe)) {
    Write-Host "[run] 未找到编译产物，先执行 scripts\build-agent.ps1"
    exit 1
}

# 不传人设目录，让 Host 自己解析（默认 data/persona/hutao）。
# 早先这里传的是 data\persona —— 那是**所有人设的父目录**，不是某个人设的目录，
# 会被 PersonaLoader 当成缺 system-prompt.md 而直接崩。
# 要指定角色就传具体目录，例如 data\persona\furina。
& $exe @args
exit $LASTEXITCODE
