# Real end-to-end UI automation for the QQNT chat client, launched in chat-only mode
# so the AutomationIds are unambiguous (no pet window alongside).
# ASCII-only source: Windows PowerShell 5.1 mis-decodes UTF-8 literals.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$global:Pass = 0; $global:Fail = 0
$global:Report = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $global:Pass++ } else { $global:Fail++ }
    $line = "$(if($ok){'PASS'}else{'FAIL'}) $name$(if($detail){"  [$detail]"})"
    $global:Report.Add($line); Write-Output $line
}

$exe = 'E:\tomorrow\AIGC\hutao-companion\hosts\HuTao.Pet\bin\Debug\net9.0-windows\HuTao.Pet.exe'
$repo = 'E:\AIGC-realtest'
$ev = "$repo\evidence"
New-Item -ItemType Directory -Path $ev -Force | Out-Null

Get-Process -Name 'HuTao.Pet' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process -FilePath $exe -ArgumentList '--chat-shell' -WorkingDirectory $repo
Start-Sleep -Seconds 8
$proc = Get-Process -Name 'HuTao.Pet' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'CHAT-SHELL-DID-NOT-START'; exit 1 }
$petPid = $proc.Id
Write-Output "chat-shell pid=$petPid"

$root = [System.Windows.Automation.AutomationElement]::RootElement
$PIDC = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $petPid)
function Find-One([string]$id) {
    $c = New-Object System.Windows.Automation.AndCondition($PIDC,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Find-Many([string]$id) {
    $c = New-Object System.Windows.Automation.AndCondition($PIDC,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Shot([string]$f) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function Get-MessageTexts {
    $scroll = Find-One 'MessageScroll'
    if (-not $scroll) { return @() }
    $els = $scroll.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)))
    return @($els | ForEach-Object { $_.Current.Name } | Where-Object { $_.Trim().Length -gt 0 })
}
function Wait-Title([string]$expect, [int]$seconds) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        Start-Sleep -Milliseconds 500
        $t = Find-One 'TitleText'
        if ($t -and $t.Current.Name -eq $expect) { return $true }
    }
    return $false
}

Write-Output ''
Write-Output '=== A. the chat window comes up on its own ==='
$sessionList = $null
for ($i = 0; $i -lt 60; $i++) { $sessionList = Find-One 'SessionList'; if ($sessionList) { break }; Start-Sleep -Seconds 1 }
Check 'A1_chat_window_rendered' ($null -ne $sessionList)
if (-not $sessionList) { $global:Report | Out-File "$ev\ui-report.txt" -Encoding utf8; Get-Process -Name 'HuTao.Pet' | Stop-Process -Force; exit 1 }

$rect = $sessionList.Current.BoundingRectangle
$onAny = $false
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
    if ($s.Bounds.IntersectsWith([System.Drawing.Rectangle]::FromLTRB(
        [int]$rect.X, [int]$rect.Y, [int]($rect.X + $rect.Width), [int]($rect.Y + $rect.Height)))) { $onAny = $true }
}
Check 'A2_window_is_on_screen' $onAny "sessionList=$rect"
$titleText = Find-One 'TitleText'
Check 'A3_active_session_has_title' ($null -ne $titleText -and $titleText.Current.Name.Length -gt 0) "title='$($titleText.Current.Name)'"
Check 'A4_single_composer' ((Find-Many 'InputBox').Count -eq 1) "inputs=$((Find-Many 'InputBox').Count)"
Check 'A5_single_send_button' ((Find-Many 'SendButton').Count -eq 1) "sends=$((Find-Many 'SendButton').Count)"
Shot "$ev\10-chat-only-opened.png"

Write-Output ''
Write-Output '=== B. sidebar: 4 migrated singles + 1 default group ==='
$items = @($sessionList.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem))))
$names = @($items | ForEach-Object { $_.Current.Name })
Check 'B1_five_sessions' ($items.Count -eq 5) "count=$($items.Count)"
Check 'B2_names_readable' ((@($names | Where-Object { $_ -match 'ViewModel' })).Count -eq 0) ($names -join ' | ')
Check 'B3_no_unread_leak_after_visit' ((@($names | Where-Object { $_ -match 'unread' })).Count -eq 0) 'accessibility names are plain titles'

Write-Output ''
Write-Output '=== C. switch sessions (looked up by accessible name) ==='
$startTitle = (Find-One 'TitleText').Current.Name
$target = $items | Where-Object { $_.Current.Name -ne $startTitle } | Select-Object -First 1
$targetName = $target.Current.Name
$target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Check 'C1_switch_changes_header' (Wait-Title $targetName 20) "'$startTitle' -> '$targetName'"
$orig = $items | Where-Object { $_.Current.Name -eq $startTitle } | Select-Object -First 1
$orig.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Check 'C2_switch_back_restores' (Wait-Title $startTitle 20) "'$targetName' -> back to '$startTitle'"
$hist = (Get-MessageTexts).Count
Check 'C3_history_rendered_after_switch_back' ($hist -gt 0) "texts=$hist"
Shot "$ev\11-after-switching.png"

Write-Output ''
Write-Output '=== D. send a real message: real DeepSeek + real GPT-SoVITS ==='
$input = Find-One 'InputBox'
$send = Find-One 'SendButton'
$probe = 'jin tian guo de zen me yang ya ' + (Get-Random -Maximum 9999)
$input.SetFocus()
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait($probe)
Start-Sleep -Milliseconds 700
$typed = $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Check 'D1_typed_text_landed_in_composer' ($typed -eq $probe) "typed='$typed'"
$before = (Get-MessageTexts).Count
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Write-Output "sent '$probe'"

$mineSeen = $false; $reply = ''
for ($i = 0; $i -lt 150; $i++) {
    Start-Sleep -Seconds 2
    $texts = Get-MessageTexts
    if (-not $mineSeen -and ($texts -contains $probe)) { $mineSeen = $true }
    $others = @($texts | Where-Object { $_ -ne $probe -and $_.Length -gt 3 })
    if ($mineSeen -and $others.Count -gt 0) { $reply = $others[-1]; break }
}
if (-not $mineSeen) {
    Write-Output 'DIAG last 6 message texts:'
    Get-MessageTexts | Select-Object -Last 6 | ForEach-Object { Write-Output "   '$_'" }
}
Check 'D2_own_message_visible_in_transcript' $mineSeen "probe='$probe'"
Check 'D3_character_replied' ($reply.Length -gt 0) "reply='$reply'"
Check 'D4_transcript_grew' ((Get-MessageTexts).Count -ge $before + 2) "before=$before after=$((Get-MessageTexts).Count)"
Shot "$ev\12-after-send.png"

Write-Output ''
Write-Output '=== E. persistence ==='
$convo = "$repo\data\conversations"
$tml = @(Get-ChildItem $convo -Recurse -Filter 'timeline.jsonl' -ErrorAction SilentlyContinue)
$total = 0; foreach ($f in $tml) { $total += @(Get-Content $f.FullName | Where-Object { $_.Trim() }).Count }
Check 'E1_timelines_persisted' ($total -gt 0) "turns=$total in $($tml.Count) sessions"
$probeOnDisk = $false
foreach ($f in $tml) { if ((Get-Content $f.FullName -Raw) -match [regex]::Escape($probe)) { $probeOnDisk = $true } }
Check 'E2_sent_message_persisted' $probeOnDisk 'found in a timeline.jsonl'
$idx = Join-Path $convo 'index.json'
Check 'E3_index_round_trips' ((Test-Path $idx) -and ((Get-Content $idx -Raw) -notmatch 'ActivePresences')) 'no derived prop leak'
$views = @(Get-ChildItem $convo -Recurse -Filter '*.jsonl' | Where-Object { $_.DirectoryName -like '*view*' })
Check 'E4_per_member_views' ($views.Count -ge 1) "files=$($views.Count)"

Write-Output ''
Write-Output '=== F. TTS produced real audio ==='
$gen = 'E:\tomorrow\AIGC\hutao-companion\data\voice\generated'
$recent = @(Get-ChildItem $gen -Filter 'agent_tts_*.wav' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-15) })
$newest = $recent | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Check 'F1_tts_generated_new_audio' ($recent.Count -gt 0) "new=$($recent.Count)"
if ($newest) { Check 'F2_audio_non_trivial' ($newest.Length -gt 20000) "$([int]($newest.Length/1024))KB $($newest.Name)" }

Write-Output ''
Write-Output '=== G. clean shutdown ==='
$closeBtn = Find-One 'CloseButton'
Check 'G1_close_button_present' ($null -ne $closeBtn)
if ($closeBtn) { Invoke-El $closeBtn }
$gone = $false
for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Seconds 1; if (-not (Get-Process -Id $petPid -ErrorAction SilentlyContinue)) { $gone = $true; break } }
Check 'G2_process_exited_on_close' $gone 'chat-only host shuts down'
$pref = "$env:LOCALAPPDATA\HuTaoCompanion\chat-shell.json"
if (Test-Path $pref) {
    $p = Get-Content $pref -Raw | ConvertFrom-Json
    $onScreen = $false
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        if ($s.Bounds.IntersectsWith([System.Drawing.Rectangle]::FromLTRB(
            [int]$p.Left, [int]$p.Top, [int]($p.Left + $p.Width), [int]($p.Top + $p.Height)))) { $onScreen = $true }
    }
    Check 'G3_saved_position_is_on_screen' $onScreen "($($p.Left),$($p.Top)) $($p.Width)x$($p.Height)"
} else { Check 'G3_saved_position_is_on_screen' $true 'no preference written (acceptable)' }

Write-Output ''
$global:Report | Out-File "$ev\ui-report.txt" -Encoding utf8
Write-Output "PASSED=$($global:Pass) FAILED=$($global:Fail)"
Get-Process -Name 'HuTao.Pet' -ErrorAction SilentlyContinue | Stop-Process -Force
exit $(if ($global:Fail -gt 0) { 1 } else { 0 })
