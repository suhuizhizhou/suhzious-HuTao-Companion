# Online evaluation of one recall arm through the FULL ReAct turn
# (retrieve -> draft -> immersion gate -> optional re-retrieve/re-draft -> answer).
#
# Why this exists next to run-arms.ps1: the offline arm matrix only measures recall
# (Anchor R@k / context recall). Multi-hop questions are not answerable from a single
# recall at all, so their real metric can only come from a live multi-round turn.
# This script also prints the per-stratum table, because the three strata do NOT share
# one acceptance standard:
#   multi_hop            -> online rounds actually happened + facts got completed
#   single_shot_retrieval-> facts complete from one recall
#   no_retrieval         -> did it avoid retrieving at all
#
# The judge invoked inside --agent-live only scores immersion / context coherence /
# logic coherence of the produced ANSWER. It makes no factual-validity claim.
param(
    [string]$Cases = 'evaluation/story-rag/benchmark-v4-agentic.jsonl',
    [int]$Limit = 100,
    [string]$Strategy = 'Baseline',
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo 'tests\HuTao.StoryRag.Eval\bin\Debug\net9.0\HuTao.StoryRag.Eval.dll'
$env:DOTNET_CLI_HOME = Join-Path $repo '.cache\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.cache\nuget'
$env:MSBuildEnableWorkloadResolver = 'false'

# The key lives in .env as UTF-8. Never use Get-Content here: Windows PowerShell 5.1
# decodes UTF-8 as the ANSI codepage and hands back an array, which is how the key
# silently became a 0-length array in an earlier run.
$envFile = Join-Path $repo '.env'
if (Test-Path $envFile) {
    $text = [System.IO.File]::ReadAllText($envFile, [System.Text.Encoding]::UTF8)
    $match = [regex]::Match($text, '(?m)^\s*DEEPSEEK_API_KEY\s*=\s*(?<v>\S+)\s*$')
    if ($match.Success) { $env:DEEPSEEK_API_KEY = $match.Groups['v'].Value }
}
if ([string]::IsNullOrWhiteSpace($env:DEEPSEEK_API_KEY)) {
    throw 'DEEPSEEK_API_KEY is empty; --agent-live cannot run. Put it in .env or the environment.'
}

if (-not (Test-Path $exe)) {
    & dotnet build (Join-Path $repo 'HuTao.Companion.sln') -v q --nologo | Out-Null
}
if (-not $Out) { $Out = ".cache\agent-$Strategy" }
$dir = Join-Path $repo $Out
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue

Push-Location $repo
try {
    Write-Host "online agent run: strategy=$Strategy cases=$Cases limit=$Limit -> $Out"
    & dotnet $exe --cases $Cases --strategy $Strategy --agent-live --agent-limit $Limit --out $dir
    if (-not (Test-Path (Join-Path $dir 'report.json'))) { throw "online run produced no report: $Out" }

    $report = [System.IO.File]::ReadAllText((Join-Path $dir 'report.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($null -eq $report.agent) { throw "report has no agent section: $Out" }

    Write-Host ''
    Write-Host 'Online strata (each stratum has its own standard, they are NOT comparable):'
    Write-Host ('{0,-22} {1,4} {2,8} {3,9} {4,9} {5,9} {6,9} {7,9} {8,9}' -f `
        'stratum', 'n', 'rounds', 'multiRt', 'factFull', 'answer', 'fallbk', 'immersv', 'gateFix')
    foreach ($name in @('multi_hop', 'single_shot_retrieval', 'no_retrieval')) {
        $s = $report.agent.strata.$name
        if ($null -eq $s) { continue }
        $immersive = 0.0
        if ($s.count -gt 0) { $immersive = $s.immersive / [double]$s.count }
        Write-Host ('{0,-22} {1,4} {2,8:F2} {3,9:P0} {4,9:P0} {5,9} {6,9} {7,9:P0} {8,9}' -f `
            $name, $s.count, $s.mean_rounds, $s.multi_round_rate, $s.fact_complete_rate, `
            $s.answered, $s.answer_fallback, $immersive, $s.gate_critic_caught)
    }
    Write-Host ''
    Write-Host "gate paths:   $($report.agent.gate_paths | ConvertTo-Json -Compress)"
    Write-Host "answer paths: $($report.agent.answer_paths | ConvertTo-Json -Compress)"
    Write-Host "judge: immersive=$($report.agent.judge_immersive) minor=$($report.agent.judge_minor_break) broken=$($report.agent.judge_broken) failed=$($report.agent.judge_failed) mean=$('{0:F3}' -f $report.agent.judge_mean_score)"
    Write-Host ''
    # Agency evidence: without these two numbers, "the agent picks its own recall channel"
    # is indistinguishable from "a fixed arm was configured" in the artifact.
    Write-Host "arm usage:    $($report.agent.arm_usage | ConvertTo-Json -Compress)"
    Write-Host "arm sources:  $($report.agent.arm_sources | ConvertTo-Json -Compress)"
    Write-Host ("agency: steps={0} agentChosen={1} casesWithAgentChoice={2} casesWithMultipleArms={3}" -f `
        $report.agent.steps_total, $report.agent.agent_chosen_steps, `
        $report.agent.cases_with_agent_choice, $report.agent.cases_with_multiple_arms)
}
finally { Pop-Location }
