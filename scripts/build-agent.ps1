# 一键构建整个 C# 解决方案（PowerShell）
#
# 模块划分见 docs/modules.md：六个库按依赖方向分层，宿主与测试在最上层。
# 解决方案文件在仓库根（HuTao.Companion.sln）。

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sln = Join-Path $root 'HuTao.Companion.sln'

if (-not (Test-Path $sln)) {
    Write-Host "[build] 找不到解决方案：$sln"
    exit 1
}

& dotnet build $sln -v q --nologo
exit $LASTEXITCODE
