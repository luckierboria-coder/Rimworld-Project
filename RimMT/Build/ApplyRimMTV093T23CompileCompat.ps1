$ErrorActionPreference = 'Stop'

$path = 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
if (!(Test-Path $path)) { throw "T23 compile compat: missing generated file $path" }
$text = Get-Content $path -Raw

# JobIssueParams is Verse.AI.JobIssueParams in RimWorld 1.5. Foundation III generated
# this source with using Verse but omitted using Verse.AI, so a clean CI compile fails.
if ($text -notmatch '(?m)^using Verse\.AI;\s*$')
{
    if ($text -notmatch '(?m)^using Verse;\s*$') { throw 'T23 compile compat: using Verse anchor missing.' }
    $text = [regex]::Replace($text, '(?m)^using Verse;\s*$', "using Verse;`r`nusing Verse.AI;", 1)
    Set-Content $path $text -Encoding UTF8
}

Write-Host 'Applied T23 compile compatibility: Verse.AI imported for JobIssueParams.'
