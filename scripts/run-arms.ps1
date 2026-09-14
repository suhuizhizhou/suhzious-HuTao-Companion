# Run every recall strategy arm into its own report dir, so strategy-report.ps1 can
# compare them case-by-case. Offline, deterministic, zero LLM calls.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo 'tests\HuTao.StoryRag.Eval\bin\Debug\net9.0\HuTao.StoryRag.Eval.dll'
$env:DOTNET_CLI_HOME = Join-Path $repo '.cache\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.cache\nuget'
$env:MSBuildEnableWorkloadResolver = 'false'
if (-not (Test-Path $exe)) {
    & dotnet build (Join-Path $repo 'HuTao.Companion.sln') -v q --nologo | Out-Null
}
Push-Location $repo
try {
    foreach ($arm in @('Baseline', 'LexicalOnly', 'ConceptOnly', 'SemanticOnly', 'HierarchyFirst', 'Fusion', 'ChapterScope')) {
        $dir = Join-Path $repo ".cache\rep-$arm"
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "running arm: $arm"
        & dotnet $exe --strategy $arm --out $dir 2>&1 | Out-Null
        if (-not (Test-Path (Join-Path $dir 'report.json'))) { throw "arm $arm produced no report" }
    }
    Write-Host 'done: .cache/rep-<arm>/report.json'
}
finally { Pop-Location }
