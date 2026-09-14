# Per-arm recall capability report.
#
# WHY THIS EXISTS
#   Different recall strategies must NOT be judged by one uniform standard.
#   Each arm has a hypothesis (what it is for), so each arm gets its own target
#   stratum and its own metric. Judging ConceptOnly by "Anchor R@1 on all cases"
#   would hide the thing it exists to do, and would reward arms for being
#   generically safe instead of specifically useful.
#
# STRATA (defined from the Baseline arm, so they are arm-independent):
#   RankedOut    = baseline anchor5 && !anchor1  -> gold reached top-5 but not top-1 (RANKING failure)
#   NeverReached = !baseline anchor5             -> gold never reached top-5      (RECALL failure)
#
# USAGE
#   scripts/run-arms.ps1            (runs every arm into .cache/rep-<arm>/report.json)
#   scripts/strategy-report.ps1
param([string]$Root = (Join-Path $PSScriptRoot '..\.cache'))

$ErrorActionPreference = 'Stop'
$arms = @('Baseline', 'LexicalOnly', 'ConceptOnly', 'SemanticOnly', 'HierarchyFirst', 'Fusion', 'ChapterScope')
$data = @{}
$declaredStrata = @{}
foreach ($a in $arms) {
    $p = Join-Path $Root "rep-$a\report.json"
    if (-not (Test-Path $p)) { throw "missing report: $p  (run scripts/run-arms.ps1 first)" }
    $o = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    # The report is self-describing: verify arm identity instead of trusting the dir name.
    if ($null -ne $o.strategy -and $o.strategy -ne $a) {
        throw "arm mismatch: expected $a but $p says '$($o.strategy)'"
    }
    $declaredStrata[$a] = $o.strata
    $m = @{}
    foreach ($c in $o.cases) { $m["$($c.id)|$($c.variant)"] = $c }
    $data[$a] = $m
}

$base = $data['Baseline']
$all = @($base.Keys)
# Strata and uniform metrics are defined ONLY over cases that actually HAVE a gold
# anchor to find. Two separate contaminations to exclude:
#   1. unanswerable cases (nothing was ever written in the corpus);
#   2. cases with no gold fact groups at all (out-of-domain / comfort / playful /
#      smalltalk ...). These are SUPPOSED to not retrieve, so counting them as
#      "never reached" inflates the miss stratum and hides the real battlefield.
$keys = @($all | Where-Object { -not $base[$_].unanswerable -and [int]$base[$_].gold_fact_groups -gt 0 })
$excluded = $all.Count - $keys.Count
$rankedOut = @($keys | Where-Object { $base[$_].anchor5 -and -not $base[$_].anchor1 })
$never = @($keys | Where-Object { -not $base[$_].anchor5 })

# The EVAL now owns the stratum definition (report.strata). This script must not silently
# drift from it: same class of failure as the arm-list desync fixed earlier - two places
# computing the same fact, one of them changing without the other noticing.
$declared = $declaredStrata['Baseline']
if ($null -ne $declared -and
    ([int]$declared.answerable_with_gold -ne $keys.Count -or
     [int]$declared.ranked_out -ne $rankedOut.Count -or
     [int]$declared.never_reached -ne $never.Count)) {
    throw ("stratum drift: eval={0}/{1}/{2} script={3}/{4}/{5}" -f `
        $declared.answerable_with_gold, $declared.ranked_out, $declared.never_reached, `
        $keys.Count, $rankedOut.Count, $never.Count)
}

function Mean1($arm, $set, $field) {
    if ($set.Count -eq 0) { return [double]::NaN }
    $s = 0.0
    foreach ($k in $set) {
        $v = $data[$arm][$k].$field
        if ($null -ne $v -and $v -eq $true) { $s += 1.0 }
    }
    return $s / $set.Count
}
function MeanN($arm, $set, $field) {
    if ($set.Count -eq 0) { return [double]::NaN }
    $s = 0.0; $n = 0
    foreach ($k in $set) {
        $v = $data[$arm][$k].$field
        if ($null -ne $v) { $s += [double]$v; $n++ }
    }
    if ($n -eq 0) { return [double]::NaN }
    return $s / $n
}
function P95($arm) {
    $t = @($data[$arm].Values | Where-Object { $_.cache_hit -eq $false -and $_.actual_route -eq 'Retrieve' } |
        ForEach-Object { [double]$_.elapsed_ms } | Sort-Object)
    if ($t.Count -eq 0) { return [double]::NaN }
    return $t[[Math]::Min($t.Count - 1, [int][Math]::Floor(0.95 * $t.Count))]
}
function F($v) { if ([double]::IsNaN($v)) { return '  n/a' } else { return ('{0,5:F1}%' -f (100 * $v)) } }

Write-Host "cases=$($all.Count)  answerable=$($keys.Count)  excluded=$excluded  RankedOut=$($rankedOut.Count)  NeverReached=$($never.Count)"
Write-Host ''
Write-Host 'WHERE THE MISSES ARE (Baseline NeverReached, top groups) - this decides what to attack next:'
foreach ($field in @('split', 'category', 'domain')) {
    Write-Host ("  by $field :")
    $never | Group-Object { $base[$_].$field } | Sort-Object Count -Descending | Select-Object -First 5 |
        ForEach-Object { Write-Host ('    {0,-26} {1,4}' -f $_.Name, $_.Count) }
}
Write-Host ''
Write-Host 'REFERENCE (uniform metrics, for orientation only - not the verdict):'
Write-Host ('{0,-16} {1,8} {2,8} {3,10} {4,9}' -f 'arm', 'A1@All', 'A5@All', 'CtxAll@All', 'P95ms')
foreach ($a in $arms) {
    Write-Host ('{0,-16} {1,8} {2,8} {3,10} {4,9:F1}' -f $a,
        (F (Mean1 $a $keys 'anchor1')), (F (Mean1 $a $keys 'anchor5')),
        (F (Mean1 $a $keys 'context_recall')), (P95 $a))
}

Write-Host ''
Write-Host 'PER-ARM STANDARD (each arm judged on the stratum it exists for):'
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'arm', 'designated capability metric', 'value')
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'ConceptOnly', 'A1@RankedOut (rank fix)', (F (Mean1 'ConceptOnly' $rankedOut 'anchor1')))
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'ConceptOnly', 'A5@NeverReached (recall fix)', (F (Mean1 'ConceptOnly' $never 'anchor5')))
# SemanticOnly is a recall-side arm too, so it is judged on the SAME stratum as ConceptOnly -
# apples to apples. But the label says "needs --semantic" on purpose: with no dense service
# configured this arm runs with its main signal ABSENT, so the number is NOT a verdict on
# semantic retrieval, only proof that the arm is wired and degrades honestly.
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'SemanticOnly', 'A5@NeverReached (needs --semantic)', (F (Mean1 'SemanticOnly' $never 'anchor5')))
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'HierarchyFirst', 'A1@RankedOut (rank fix)', (F (Mean1 'HierarchyFirst' $rankedOut 'anchor1')))
# ChapterScope is a TWO-LEVEL arm, so its designated test is the two_level probe, NOT the
# single-call matrix row. Measured fact: in the single-call matrix ChapterScope is identical
# to Baseline on every column (summary-level chapter hints never enter top-5 at TopK=5),
# so quoting that row would be a fake "no difference". The probe below replays the real
# mechanism: level 1 = Baseline locate -> level 2 = expand the chapters level 1 landed in.
$twoLevel = $null
foreach ($a in $arms) {
    $p = Join-Path $Root "rep-$a\report.json"
    if (-not (Test-Path $p)) { continue }
    $o = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($null -ne $o.two_level) { $twoLevel = $o.two_level; break }
}
if ($null -ne $twoLevel) {
    $l1 = [int]$twoLevel.fact_groups_complete_level1
    $l2 = [int]$twoLevel.fact_groups_complete_level2
    $n = [int]$twoLevel.executions
    Write-Host ('{0,-16} {1,-34} {2,10}' -f 'ChapterScope', '2-level: all-facts complete (L1)', ("{0}/{1}" -f $l1, $n))
    Write-Host ('{0,-16} {1,-34} {2,10}' -f 'ChapterScope', '2-level: all-facts complete (L2)', ("{0}/{1}" -f $l2, $n))
    Write-Host ('{0,-16} {1,-34} {2,10}' -f 'ChapterScope', '2-level: gained / lost', ("+{0} / -{1}" -f [int]$twoLevel.gained_by_second_level, [int]$twoLevel.lost_by_second_level))
} else {
    Write-Host ('{0,-16} {1,-34} {2,10}' -f 'ChapterScope', '2-level probe', 'n/a (no report)')
}
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'ChapterScope', 'single-call row (NOT informative)', 'see two_level above')
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'LexicalOnly', 'A1@All vs Baseline (lower bound)', (F (Mean1 'LexicalOnly' $keys 'anchor1')))
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'LexicalOnly', 'miss rate = !A5@All', (F (1 - (Mean1 'LexicalOnly' $keys 'anchor5'))))
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'Baseline', 'miss rate = !A5@All', (F (1 - (Mean1 'Baseline' $keys 'anchor5'))))

# Fusion is judged by RESCUE (what no single arm got) and by COST, not by average quality.
$single = @('LexicalOnly', 'ConceptOnly', 'HierarchyFirst')
$rescued = 0; $lostByAll = 0
foreach ($k in $keys) {
    $anySingle = $false
    foreach ($s in $single) { if ($data[$s][$k].anchor5) { $anySingle = $true; break } }
    if (-not $anySingle) {
        $lostByAll++
        if ($data['Fusion'][$k].anchor5) { $rescued++ }
    }
}
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'Fusion', "rescue rate (all single arms missed)", (F ($(if ($lostByAll -eq 0) { [double]::NaN } else { $rescued / $lostByAll }))))
Write-Host ('{0,-16} {1,-34} {2,10}' -f 'Fusion', 'cost = P95 / Baseline P95', ('{0:F2}x' -f ((P95 'Fusion') / (P95 'Baseline'))))
