@echo off
rem CADPeta AutoCAD plugin installer (no administrator rights required).
rem Copies CADPeta.bundle to %APPDATA%\Autodesk\ApplicationPlugins.
rem After install, PETA / PETADIAG commands become available on next AutoCAD startup.
rem Japanese guide: see README-install.txt
setlocal
set "SRC=%~dp0CADPeta.bundle"
set "DEST=%APPDATA%\Autodesk\ApplicationPlugins\CADPeta.bundle"

if not exist "%SRC%\PackageContents.xml" (
  echo [ERROR] CADPeta.bundle not found next to this script: "%SRC%"
  goto :failed
)

echo Installing CADPeta to:
echo   %DEST%

if exist "%DEST%" (
  echo Removing existing installation...
  rmdir /S /Q "%DEST%"
  if exist "%DEST%" (
    echo [ERROR] Could not remove existing folder. Close AutoCAD and try again.
    goto :failed
  )
)

xcopy /E /I /Y "%SRC%" "%DEST%" > nul
if errorlevel 1 (
  echo [ERROR] Copy failed.
  goto :failed
)

echo.
echo [DONE] Install succeeded. Start AutoCAD and type PETA at the command line.
echo        If SECURELOAD blocks loading, see README-install.txt.
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 0

:failed
echo.
echo [FAILED] Install did not complete. See the message above.
echo          Japanese guide: README-install.txt
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 1
