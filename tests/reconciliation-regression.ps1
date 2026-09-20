$ErrorActionPreference='Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '..\wechat-daemon.exe')) | Out-Null
function Strings($items) {
    $result=New-Object 'Collections.Generic.List[string]'
    foreach($item in $items){$result.Add($item)}
    return ,$result
}
$stored=Strings @('older context','one','two','three','four')
$anchor=[WeChatSidekick.Backend.Reconciliation]::CreateAnchor($stored)
$phone=Strings @('older context','one','two','three','four','phone reply','phone reply')
$merged=[WeChatSidekick.Backend.Reconciliation]::MergeAtAnchor($stored,$phone,$anchor)
if(($merged -join '|') -cne ($phone -join '|')){throw 'Phone-only records missing or legitimate repeat lost.'}
$again=[WeChatSidekick.Backend.Reconciliation]::MergeAtAnchor($merged,$phone,$anchor)
if(($again -join '|') -cne ($merged -join '|')){throw 'Repeated backfill changed records.'}
# Desktop live capture may see a later message while missing phone replies in between.
$live=Strings @('older context','one','two','three','four','desktop reply')
$caughtUp=Strings @('older context','one','two','three','four','phone reply','desktop reply')
$actual=[WeChatSidekick.Backend.Reconciliation]::MergeAtAnchor($live,$caughtUp,$anchor)
if(($actual -join '|') -cne ($caughtUp -join '|')){throw 'Live capture and backfill did not reconcile gap.'}
$rejected=$false
try{[WeChatSidekick.Backend.Reconciliation]::MergeAtAnchor($live,$phone,$anchor)|Out-Null}catch{$rejected=$true}
if(!$rejected){throw 'Saved later message was discarded.'}
$repeated=Strings @('a','b','c','a','b','c')
if([WeChatSidekick.Backend.Reconciliation]::FindAnchorEnd($repeated,(Strings @('a','b','c'))) -ne -1){throw 'Ambiguous anchor accepted.'}
if([WeChatSidekick.Backend.Reconciliation]::FindAnchorEnd((Strings @('图片','图片','图片')),(Strings @('图片','图片','图片'))) -ne -1){throw 'Image-only anchor accepted.'}
$now=[DateTime]::new(2026,9,19)
foreach($case in @(
 @{Label="name`npreview`n昨天 14:26`n";Since=$now.AddDays(-1);Expected=$true},
 @{Label="name`npreview`n星期三`n";Since=$now.AddDays(-5);Expected=$true},
 @{Label="name`npreview`n09/16`n";Since=$now.AddDays(-5);Expected=$true},
 @{Label="name`n昨天`n09/01`n";Since=$now.AddDays(-1);Expected=$false},
 @{Label="name`npreview`nunknown-date`n";Since=$now;Expected=$true}
)){
 if([WeChatSidekick.Backend.Reconciliation]::Eligible($case.Label,$case.Since,$now) -ne $case.Expected){throw 'Activity cutoff failed.'}
}
'PASS: phone-only replies; legitimate repeats; idempotence; desktop gap repair; loss prevention; ambiguous/media anchors; missed-day dates.'
$before=[DateTime]::new(2026,9,18,15,0,0)
$after=[DateTime]::new(2026,9,19,15,0,0)
if(![WeChatSidekick.Backend.Reconciliation]::SameRecord('[system]: 昨天 14:22',$before,'[system]: 星期四 14:22',$after)){throw 'Relative date rollover mismatch.'}
if([WeChatSidekick.Backend.Reconciliation]::SameRecord('[system]: 昨天 14:22',$before,'[system]: 星期四 14:23',$after)){throw 'Different timestamps conflated.'}
if([WeChatSidekick.Backend.Reconciliation]::SameRecord('meet at 14:22',$before,'[system]: 星期四 14:22',$after)){throw 'Message content mistaken for timestamp.'}
if([WeChatSidekick.Backend.Reconciliation]::SameRecord('[system]: 昨天 14:22',$before,'[system]: 昨天 14:22',$after)){throw 'Same relative label on different days conflated.'}
'PASS: relative date rollover retains identity; different times and ordinary text remain distinct.'
$historical=Strings @('[system]: 昨天 14:22','one','[system]: 8月27日 01:23','two','three')
$current=Strings @('[system]: 星期四 14:22','one','two','three','phone reply')
$dates=New-Object 'Collections.Generic.List[datetime]'
foreach($item in $historical){$dates.Add($before)}
$preserved=[WeChatSidekick.Backend.Reconciliation]::PreserveSeparators($historical,$dates,$current,$after)
if(($preserved -join '|') -cne (($historical -join '|')+'|phone reply')){throw 'Layout-dependent separator retention failed.'}
$unchanged=[WeChatSidekick.Backend.Reconciliation]::MergeAtAnchor($preserved,$current,([WeChatSidekick.Backend.Reconciliation]::CreateAnchor($preserved)))
if(($unchanged -join '|') -cne ($preserved -join '|')){throw 'Checkpoint repeat reinterpreted historical date labels.'}
$rejected=$false
try{[WeChatSidekick.Backend.Reconciliation]::PreserveSeparators($historical,$dates,(Strings @('one','three')),$after)|Out-Null}catch{$rejected=$true}
if(!$rejected){throw 'Ordinary message omission was accepted as metadata.'}
'PASS: omitted separators retained; historical prefix unchanged on repeat; missing ordinary records rejected.'
