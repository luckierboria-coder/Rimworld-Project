param([Parameter(Mandatory=$true)][string]$ModRoot)
$ErrorActionPreference='Stop'
$files = @(
  "$ModRoot\Source\Core\JobDriver_CavalryRally.cs",
  "$ModRoot\Source\Core\JobDriver_CavalryCharge.cs"
)
foreach ($path in $files) {
  $text = Get-Content $path -Raw
  $text = $text.Replace('protected override IEnumerable<Toil> MakeNewToils()', 'public override IEnumerable<Toil> MakeNewToils()')
  Set-Content $path $text -Encoding UTF8
}
Write-Host '[GUCC 1.5] JobDriver MakeNewToils access corrected for RimWorld 1.5.'
