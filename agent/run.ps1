# 一键运行 Agent 演示（PowerShell）
$exe = Join-Path $PSScriptRoot 'src\HuTao.Agent.Host\bin\Debug\net9.0\HuTao.Agent.Host.exe'
$persona = Join-Path (Split-Path $PSScriptRoot -Parent) 'data\persona'

if (-not (Test-Path $exe)) {
    Write-Host "[run] 未找到编译产物，先执行 build.ps1"
    exit 1
}
& $exe $persona
