param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'

$p = Join-Path $Root 'src/Smf.Mod/Rendering/Shaders/GuiShaderBundleAssembler.cs'
$text = (Get-Content $p -Raw).Replace("`r`n", "`n")
$old = '                string name = Field(value, "m_Name").AsString;'
$new = '                string name = Field(value, "m_ParsedForm/m_Name").AsString;'
if (-not $text.Contains($old)) {
    throw 'SMF15 alpha3 resolver anchor not found; refusing to build.'
}
$text = $text.Replace($old, $new)
[IO.File]::WriteAllText($p, $text, [Text.UTF8Encoding]::new($false))

Write-Host 'Applied SMF RW1.5 alpha3 shader-name resolver fix: resolve built-in Shader identity from m_ParsedForm/m_Name, matching upstream validation.'
