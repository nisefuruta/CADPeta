@echo off
rem CADPeta AutoCAD plugin uninstaller.
rem Removes %APPDATA%\Autodesk\ApplicationPlugins\CADPeta.bundle.
rem Japanese guide: see README-install.txt
setlocal
set "DEST=%APPDATA%\Autodesk\ApplicationPlugins\CADPeta.bundle"

if not exist "%DEST%" (
  echo CADPeta is not installed: "%DEST%"
  echo.
  echo [DONE] Nothing to remove.
  goto :succeeded
)

echo Removing CADPeta from:
echo   %DEST%
rmdir /S /Q "%DEST%"
if exist "%DEST%" (
  echo [ERROR] Could not remove. Close AutoCAD and try again.
  goto :failed
)

echo.
echo [DONE] Uninstall succeeded.

:succeeded
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 0

:failed
echo.
echo [FAILED] Uninstall did not complete. See the message above.
echo          Japanese guide: README-install.txt
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 1
