@echo off
setlocal
cd /d "%~dp0"

if not exist "1.5\Textures" mkdir "1.5\Textures"

certutil -f -decode "BinaryBase64\spt.png.b64" "1.5\Textures\spt.png" >nul
if errorlevel 1 (
  echo Failed to restore 1.5\Textures\spt.png
  exit /b 1
)

echo Restored: 1.5\Textures\spt.png
echo.
echo Next run Build_RimWorld_1.5.bat to compile LovinAnywhere.dll against your RimWorld 1.5 installation.
exit /b 0
