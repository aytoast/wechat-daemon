param(
    [Parameter(Mandatory=$true)][string]$RecordPath,
    [Parameter(Mandatory=$true)][string]$EvidencePath,
    [string]$PreviousRecordPath
)
$ErrorActionPreference = 'Stop'
$records = Get-Content -LiteralPath $RecordPath -Raw -Encoding UTF8 | ConvertFrom-Json
$evidence = Get-Content -LiteralPath $EvidencePath -Raw -Encoding UTF8 | ConvertFrom-Json
$actual = @($records | ForEach-Object { $_.Text -replace '^\[(me|other|system)\]: ','' })
$expected = @($evidence.records.text)
if ($actual.Count -ne $expected.Count) { throw "Count mismatch: actual=$($actual.Count), expected=$($expected.Count)" }
for ($i=0; $i -lt $expected.Count; $i++) {
    if ($actual[$i] -cne $expected[$i]) { throw "Text or order mismatch at index $i" }
}
if (@($records.Id | Sort-Object -Unique).Count -ne $records.Count) { throw 'Duplicate record IDs.' }
$stableIds = $null
if ($PreviousRecordPath) {
    $previous = Get-Content -LiteralPath $PreviousRecordPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $stableIds = ($previous.Id -join '|') -ceq ($records.Id -join '|')
    if (!$stableIds) { throw 'Record IDs changed during repeated capture.' }
}
[PSCustomObject]@{
    MatchedRecords=$records.Count
    ExactTextAndOrder=$true
    UniqueIds=$true
    StableIds=$stableIds
    Hash=(Get-FileHash -LiteralPath $RecordPath -Algorithm SHA256).Hash
    UnclassifiedSenders=@($records | Where-Object { $_.Text -notmatch '^\[(me|other|system)\]: ' }).Count
} | ConvertTo-Json
