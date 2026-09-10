# 一键构建 Agent（PowerShell）
# 说明：本机 dotnet 的 workload 解析器目录损坏，C 盘 .dotnet 只读，须重定向到项目内。
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:MSBuildEnableWorkloadResolver = 'false'

Set-Location $PSScriptRoot
dotnet build src/HuTao.Agent.Host/HuTao.Agent.Host.csproj -c Debug
dotnet build src/HuTao.Pet/HuTao.Pet.csproj -c Debug
