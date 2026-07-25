@echo off
rem CADPeta compat-CAD (ARES / IJCAD / BricsCAD) auto-load removal.
rem No administrator rights required. Delegates to unregister.ps1 next to this file.
rem This window always stays open until you press a key, even on failure.
rem Japanese guide: see README-compat.txt
setlocal
set "PS1=%~dp0unregister.ps1"
set "PSEXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

if not exist "%PS1%" (
  echo [ERROR] unregister.ps1 not found next to this script: "%PS1%"
  echo         Run this file inside the extracted zip folder.
  goto :failed
)

"%PSEXE%" -NoProfile -NoLogo -ExecutionPolicy Bypass -File "%PS1%" -NoPause %*
if errorlevel 1 goto :failed

echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 0

:failed
echo.
echo [FAILED] Uninstall did not complete. See the message above.
echo          Japanese guide: README-compat.txt
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 1
