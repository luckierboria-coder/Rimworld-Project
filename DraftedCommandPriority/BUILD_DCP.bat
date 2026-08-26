@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title Drafted Command Priority V0.1 - Local Builder
cd /d "%~dp0"

set "ROOT=%CD%"
set "PROJECT=%ROOT%\Source\DraftedCommandPriority\DraftedCommandPriority.csproj"
set "DLL=%ROOT%\1.5\Assemblies\DraftedCommandPriority.dll"
set "OUT=%ROOT%\BuildOutput"
set "STAGE=%OUT%\DraftedCommandPriority"
set "ZIP=%OUT%\DraftedCommandPriority_V0.1_Playtest.zip"
set "RW=F:\Rimworld\RimWorld"

cls
echo ============================================================
echo  Drafted Command Priority V0.1 - Local Builder
echo ============================================================
echo.

if not exist "%PROJECT%" (
  echo [ERROR] Project file not found:
  echo %PROJECT%
  echo.
  pause
  exit /b 1
)

set "DOTNET=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
  if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
  ) else if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
  ) else (
    echo [ERROR] .NET SDK was not found.
    echo Install the .NET 8 SDK, then run BUILD_DCP.bat again.
    echo This project targets .NET Framework 4.7.2 but uses the .NET SDK to restore/build.
    echo.
    pause
    exit /b 2
  )
)

"%DOTNET%" --list-sdks > "%TEMP%\dcp_sdks.txt" 2>nul
for %%A in ("%TEMP%\dcp_sdks.txt") do if %%~zA==0 (
  echo [ERROR] dotnet is present, but no .NET SDK is installed.
  echo Install the .NET 8 SDK, then run BUILD_DCP.bat again.
  echo.
  del "%TEMP%\dcp_sdks.txt" >nul 2>nul
  pause
  exit /b 3
)
del "%TEMP%\dcp_sdks.txt" >nul 2>nul

echo [1/5] Restoring NuGet references...
"%DOTNET%" restore "%PROJECT%"
if errorlevel 1 goto :buildfail

echo.
echo [2/5] Building Release DLL...
if exist "%DLL%" del /f /q "%DLL%" >nul 2>nul
"%DOTNET%" build "%PROJECT%" --configuration Release --no-restore
if errorlevel 1 goto :buildfail

if not exist "%DLL%" (
  echo.
  echo [ERROR] Build reported success but DLL was not produced:
  echo %DLL%
  goto :buildfail
)

echo.
echo [3/5] Staging RimWorld mod...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%" >nul 2>nul
xcopy "%ROOT%\About" "%STAGE%\About\" /e /i /q /y >nul
xcopy "%ROOT%\Languages" "%STAGE%\Languages\" /e /i /q /y >nul
xcopy "%ROOT%\1.5" "%STAGE%\1.5\" /e /i /q /y >nul
copy /y "%ROOT%\README.md" "%STAGE%\README.md" >nul

if not exist "%STAGE%\1.5\Assemblies\DraftedCommandPriority.dll" (
  echo [ERROR] Staging failed: compiled DLL is missing.
  goto :buildfail
)

echo.
echo [4/5] Creating playtest ZIP...
if not exist "%OUT%" mkdir "%OUT%" >nul 2>nul
if exist "%ZIP%" del /f /q "%ZIP%" >nul 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%STAGE%' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto :buildfail
if not exist "%ZIP%" goto :buildfail

echo.
echo [5/5] Verifying output...
for %%F in ("%DLL%") do echo DLL: %%~fF  ^(%%~zF bytes^)
for %%F in ("%ZIP%") do echo ZIP: %%~fF  ^(%%~zF bytes^)
where certutil >nul 2>nul
if not errorlevel 1 (
  echo.
  echo SHA256:
  certutil -hashfile "%ZIP%" SHA256 | findstr /v /c:"CertUtil"
)

echo.
echo ============================================================
echo  BUILD SUCCESS
 echo ============================================================
echo.
echo Playtest package:
echo %ZIP%
echo.

if exist "%RW%\RimWorldWin64.exe" if exist "%RW%\Mods" (
  echo Detected RimWorld 1.5 install:
  echo %RW%
  echo.
  choice /c YN /n /m "Copy this build to RimWorld\Mods\DraftedCommandPriority now? [Y/N] "
  if errorlevel 2 goto :done
  if errorlevel 1 goto :install
)

goto :done

:install
set "TARGET=%RW%\Mods\DraftedCommandPriority"
if not exist "%TARGET%" mkdir "%TARGET%" >nul 2>nul
xcopy "%STAGE%\*" "%TARGET%\" /e /i /q /y >nul
if errorlevel 1 (
  echo.
  echo [WARNING] Build succeeded, but automatic copy to RimWorld failed.
  echo You can manually extract/copy the ZIP from BuildOutput.
  goto :done
)
echo.
echo Installed to:
echo %TARGET%
echo Enable "Drafted Command Priority" after Harmony in the mod list.

goto :done

:buildfail
echo.
echo ============================================================
echo  BUILD FAILED
 echo ============================================================
echo.
echo Copy the error text from this window and send it to me.
echo.
pause
exit /b 10

:done
echo.
echo You can close this window.
pause
exit /b 0
