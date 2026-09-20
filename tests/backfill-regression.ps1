$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '..\wechat-daemon.exe')) | Out-Null
$type = [WeChatSidekick.Backend.WeChatAutomationService]
$filter = $type.GetMethod('IsRecentSession', [Reflection.BindingFlags]'NonPublic,Static')
$cases = @(
    @{Text="自动华`n已置顶`npreview`n昨天 14:26`n"; Expected=$true},
    @{Text="contact`npreview`nYesterday 14:26`n"; Expected=$true},
    @{Text="contact`npreview`n昨天`n"; Expected=$true},
    @{Text="contact`npreview`n15:03`n"; Expected=$true},
    @{Text="contact`n昨天`n星期三`n"; Expected=$false},
    @{Text="contact`n15:03`n08/14`n"; Expected=$false}
)
foreach ($case in $cases) {
    if ($filter.Invoke($null,@($case.Text)) -ne $case.Expected) { throw "Date filter mismatch: $($case.Text)" }
}
function Strings($items) {
    $list = New-Object 'Collections.Generic.List[string]'
    foreach ($item in $items) { $list.Add($item) }
    return ,$list
}
$older = Strings @('[other]: 我是自动华','[other]: 我是自动华','[system]: date','[me]: hello')
$newer = Strings @('[system]: date','[me]: hello','[other]: world')
$joined = [WeChatSidekick.Backend.WeChatAutomationService]::JoinOlderPage($older,$newer)
if ($joined.Count -ne 5 -or @($joined | Where-Object { $_ -eq '[other]: 我是自动华' }).Count -ne 2) { throw 'Legitimate duplicate was lost.' }
$again = [WeChatSidekick.Backend.WeChatAutomationService]::JoinOlderPage($older,$joined)
if (($again -join '|') -ne ($joined -join '|')) { throw 'Repeated viewport was duplicated.' }
$gapRejected = $false
try { [WeChatSidekick.Backend.WeChatAutomationService]::JoinOlderPage((Strings @('unrelated')),$newer) | Out-Null } catch { $gapRejected=$true }
if (!$gapRejected) { throw 'Viewport gap was accepted.' }
'PASS: 6 date cases; legitimate duplicate; repeated viewport; gap rejection.'

$night = New-Object WeChatSidekick.Backend.NightBackfillState
$now = [DateTime]::UtcNow
if (!$night.CanAttempt('test-window', $now)) { throw 'Initial schedule attempt denied.' }
$night.LastAttemptUtc = $now.ToString('o')
if ($night.CanAttempt('test-window', $now.AddMinutes(14))) { throw 'Failure retry was not delayed.' }
if (!$night.CanAttempt('test-window', $now.AddMinutes(16))) { throw 'Failure retry suppressed.' }
$night.CompletedSchedule = 'test-window'
if ($night.CanAttempt('test-window', $now.AddDays(1))) { throw 'Completed schedule repeated.' }
if (!$night.CanAttempt('next-window', $now.AddDays(1))) { throw 'Next schedule suppressed.' }
$backend = [WeChatSidekick.Backend.BackendProgram]
$within = $backend.GetMethod('IsWithinBackfillWindow',[Reflection.BindingFlags]'NonPublic,Static')
$settings = New-Object WeChatSidekick.Backend.BackfillSettings
$settings.StartMinutes=1380; $settings.EndMinutes=120
foreach ($case in @(@{Hour=23;Expected=$true},@{Hour=1;Expected=$true},@{Hour=2;Expected=$false},@{Hour=12;Expected=$false})) {
    $date=[DateTime]::Today.AddHours($case.Hour)
    if ($within.Invoke($null,@($date,$settings.PSObject.BaseObject)) -ne $case.Expected) { throw 'Overnight window boundary failed.' }
}
if (![WeChatSidekick.MessageProcessor]::IsIgnoredNode("文件`n进度: 50%")) { throw 'Transfer progress not excluded.' }
$rect=New-Object WeChatSidekick.Win32Helper+RECT
$rect.Left=100; $rect.Right=500; $rect.Top=100; $rect.Bottom=32000
$visible=[WeChatSidekick.Win32Helper]::VisibleScreenPart($rect)
if ($visible.Bottom -ge 32000 -or $visible.Bottom -le $visible.Top) { throw 'Scroll bounds not clipped.' }
'PASS: retry/completion state; overnight boundaries; upload progress filtering; physical scroll bounds.'
$capture=New-Object WeChatSidekick.ScreenCapture(0,0,10,10)
try {
    $outside=New-Object WeChatSidekick.Win32Helper+RECT
    $outside.Left=0; $outside.Right=10; $outside.Top=10000; $outside.Bottom=10020
    if ($null -ne [WeChatSidekick.Win32Helper]::IsMessageFromMe($capture,$outside)) { throw 'Offscreen sender was guessed.' }
} finally { $capture.Dispose() }
'PASS: offscreen sender remains unknown.'
