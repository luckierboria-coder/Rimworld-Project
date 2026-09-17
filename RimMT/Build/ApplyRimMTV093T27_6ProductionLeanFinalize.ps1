$ErrorActionPreference='Stop'

# Final production-lean cleanup after all historical transforms have run.
# T15/T16 diagnostics are now owned by RimMT Diagnostics v0.3, so the production runtime must
# not initialize the old temporary detail manager or poll its coordinator every main-thread frame.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=[regex]::Replace($boot,'(?m)^\s*WorkGiverDetailPatches\.Initialize\(harmony\);\s*\r?\n','')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath='RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$runtime=[regex]::Replace($runtime,'(?m)^\s*WorkGiverDeepAttribution093T15\.OnMainThreadFrame\(\);\s*\r?\n','')
Set-Content $runtimePath $runtime -Encoding UTF8

# T15 hooks embedded in diagnostic-only source files are inert because their Harmony installs are
# removed. Do not rewrite those files here; keeping historical source buildable avoids a risky
# dependency surgery while removing every resident production entry point.

Write-Host 'Finalized T27.6 production lean: removed T15 detail-manager initialization and per-frame coordinator poll.'
