# 一键运行 WPF 桌宠（PowerShell）
#
# 桌宠是「宿主」而不是「前端」：它走 .NET + WPF + 原生 Windows API，
# 与 frontend/ 预留的 Web UI 没有共享构建链。见 docs/modules.md §6.1。

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'hosts\HuTao.Pet\bin\Debug\net9.0-windows\HuTao.Pet.exe'

if (-not (Test-Path $exe)) {
    Write-Host "[run-pet] 未找到编译产物，先执行 scripts\build-agent.ps1"
    exit 1
}

# 桌宠需要在仓库根作为工作目录启动，才能定位 data/ 与 voice/
Start-Process -FilePath $exe -WorkingDirectory $root
