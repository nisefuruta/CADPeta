@echo off
rem CADPeta uninstaller for AutoCAD.
rem Deletes %APPDATA%\Autodesk\ApplicationPlugins\CADPeta.bundle.
rem This file is ASCII only, so it does not depend on the console code page. All messages are English.
rem A path is shown on its own line by a for loop over the quoted path,
rem so that special characters in the path (ampersand, caret, exclamation mark, parentheses) are not parsed.
rem Japanese guide: see README-install.txt.
rem Nothing is changed until every DLL under CADPeta.bundle and CADPeta.bundle.old can be opened.
rem A CADPeta.bundle with PackageContents.xml is the installed CADPeta. The Name and ProductCode in PackageContents.xml are not read:
rem whether PackageContents.xml exists is the only thing that tells an installed CADPeta.bundle from a leftover.
rem Leftovers are told by the folder name only, not by the files in them: CADPeta.bundle.old, CADPeta.bundle.new and a CADPeta.bundle
rem without PackageContents.xml are made only by CADPeta, so they are deleted with everything in them, including files a user put there.
rem The folder is renamed to CADPeta.bundle.old first, then deleted. The rename fails while AutoCAD has the plug-in loaded,
rem so the folder is never half deleted in that case. PackageContents.xml is deleted first so that install.bat does not take a half-deleted folder
rem for a previous install. If that file cannot be deleted, nothing else is deleted and the folder is renamed back.
rem If the folder cannot be deleted completely, the folder is shown so that the user can delete it.
rem A leftover CADPeta.bundle.new (partial copy of an interrupted install) is deleted too.
rem The DLL check (between the "rem ---- DLL check" lines) and the folder operations (between the "rem ---- folder operations" lines)
rem are the same in install.bat and uninstall.bat,
rem and the unit tests (InstallerBatchTests) fail if they differ.
rem No path is passed to "call" (neither to another batch file nor to a label): call expands % in its arguments a second time,
rem so a path that contains text like %NAME% (in APPDATA or in the folder of the zip) would be read as a different folder.
rem Delayed expansion is turned off: when the parent cmd was started with /V:ON (or DelayedExpansion is 1 in the registry),
rem an exclamation mark in a path would be removed by delayed expansion.
setlocal EnableExtensions DisableDelayedExpansion
set "PARENT=%APPDATA%\Autodesk\ApplicationPlugins"
set "DESTNAME=CADPeta.bundle"
set "DEST=%PARENT%\%DESTNAME%"
set "OLDNAME=CADPeta.bundle.old"
set "OLD=%PARENT%\%OLDNAME%"
set "NEW=%APPDATA%\Autodesk\CADPeta.bundle.new"
set "INUSE="
set "OLD_DELETED="

rem Nothing is changed until this check passes.
rem ---- DLL check: INUSE is set to the first DLL under CADPeta.bundle or CADPeta.bundle.old that cannot be opened.
rem The file is opened for append without writing, so it is not changed. The loop is written here, not in a "call :label", so that the path is expanded only once.
rem AutoCAD keeps the DLLs open for a few seconds after its window has closed (about 2 seconds with AutoCAD 2027), so the check of all DLLs
rem is tried up to 10 times, about 1 second apart, before INUSE is reported. ping waits, because timeout does not work when standard input is redirected.
set "DLLTRY=1"
:dll_check_try
set "INUSE="
if exist "%DEST%" for /r "%DEST%" %%F in (*.dll) do if not defined INUSE (>>"%%F" (call )) 2>nul || set "INUSE=%%F"
if exist "%OLD%" for /r "%OLD%" %%F in (*.dll) do if not defined INUSE (>>"%%F" (call )) 2>nul || set "INUSE=%%F"
if not defined INUSE goto :dll_check_done
if %DLLTRY% geq 10 goto :dll_check_done
if %DLLTRY% equ 1 echo A CADPeta file is in use. Waiting for AutoCAD to finish closing (up to about 10 seconds)...
set /a DLLTRY+=1
ping -n 2 127.0.0.1 > nul
goto :dll_check_try
:dll_check_done
rem ---- end of the DLL check
if defined INUSE goto :in_use_file

rem Delete a leftover CADPeta.bundle.new.
if not exist "%NEW%" goto :new_checked
echo Deleting a partial copy left by an earlier install...
set "FOLDER=%NEW%"
call :delete_folder
if errorlevel 1 goto :new_failed

:new_checked
rem Delete a leftover CADPeta.bundle.old of an interrupted install or uninstall.
if not exist "%OLD%" goto :check_installed
echo Deleting a leftover folder from an earlier install or uninstall...
set "FOLDER=%OLD%"
call :delete_folder
if errorlevel 1 goto :old_failed
set "OLD_DELETED=1"

:check_installed
if exist "%DEST%" goto :remove
echo CADPeta is not installed:
for %%P in ("%DEST%") do echo(    %%~P
echo.
if defined OLD_DELETED goto :only_old
echo [DONE] Nothing to remove.
goto :succeeded

:only_old
echo [DONE] Deleted the leftover folder:
for %%P in ("%OLD%") do echo(    %%~P
goto :succeeded

:remove
echo Removing CADPeta from:
for %%P in ("%DEST%") do echo(    %%~P
set "FOLDER=%DEST%"
set "NEWNAME=%OLDNAME%"
call :rename_folder
if errorlevel 1 goto :in_use
if not exist "%OLD%\PackageContents.xml" goto :manifest_deleted
rem The error message of del (standard error) is not hidden, so that the reason given by Windows is shown as it is.
del /F /Q "%OLD%\PackageContents.xml" > nul
if not exist "%OLD%\PackageContents.xml" goto :manifest_deleted
echo [ERROR] Could not delete PackageContents.xml. The reason given by Windows is shown above.
echo         Nothing else was deleted.
set "FOLDER=%OLD%"
set "NEWNAME=%DESTNAME%"
call :rename_folder
if errorlevel 1 goto :undo_failed
echo         The folder was renamed back to CADPeta.bundle. CADPeta is still installed.
goto :failed

:manifest_deleted
set "FOLDER=%OLD%"
call :delete_folder
if errorlevel 1 goto :partial

echo.
echo [DONE] Uninstall succeeded.

:succeeded
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 0

:partial
echo [ERROR] CADPeta was moved to the following folder, but that folder could not be deleted completely:
for %%P in ("%OLD%") do echo(    %%~P
echo         Close AutoCAD and delete that folder yourself.
goto :failed

:undo_failed
echo [ERROR] Could not remove CADPeta, and could not move it back from:
for %%P in ("%OLD%") do echo(    %%~P
echo         That folder is still a complete version. Close AutoCAD, then rename it to CADPeta.bundle to keep CADPeta,
echo         or delete it to remove CADPeta.
goto :failed

:in_use_file
echo [ERROR] An installed CADPeta file cannot be opened:
for %%P in ("%INUSE%") do echo(    %%~P
echo         AutoCAD is probably running with CADPeta loaded, or the file is read-only.
echo         Close AutoCAD and run this again. The installation was not changed.
goto :failed

:new_failed
echo [ERROR] Could not delete the partial copy left by an earlier install:
for %%P in ("%NEW%") do echo(    %%~P
echo         Close AutoCAD, delete that folder, and run this again. The installation was not changed.
goto :failed

:old_failed
echo [ERROR] Could not delete the folder left by an earlier install or uninstall:
for %%P in ("%OLD%") do echo(    %%~P
echo         Close AutoCAD, delete that folder, and run this again.
goto :failed

:in_use
echo [ERROR] Could not remove. AutoCAD with CADPeta loaded, or another program, is using a file in it.
echo         Close AutoCAD and run this again. CADPeta was not removed.
goto :failed

:failed
echo.
echo [FAILED] Uninstall did not complete. See the message above.
echo          Japanese guide: README-install.txt
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 1

rem ---- folder operations: rename, move or delete a folder, trying again while another program holds a file in it.
rem A folder whose files were just written can be held open for a few seconds by another program (an antivirus, Windows Search,
rem a sync client), and renaming, moving or deleting the folder fails while a file in it is open. Each operation is tried up to
rem 10 times, about 1 second apart. ping waits, because timeout does not work when standard input is redirected.
rem The paths are given in the variables FOLDER, NEWNAME and TARGET, not as arguments of call (call expands % in its arguments a second time).
rem errorlevel is 0 when the operation is done and 1 when it still fails after the last try. Only the last try shows the reason given by Windows.
rem :move_folder fails without moving when TARGET already exists (checked before every try), because move would put FOLDER inside it.
:rename_folder
set "TRY=1"
:rename_folder_try
if %TRY% geq 10 goto :rename_folder_last
ver > nul
ren "%FOLDER%" "%NEWNAME%" 2> nul
if not errorlevel 1 if not exist "%FOLDER%" exit /b 0
call :wait_folder
goto :rename_folder_try
:rename_folder_last
ver > nul
ren "%FOLDER%" "%NEWNAME%"
if errorlevel 1 exit /b 1
if exist "%FOLDER%" exit /b 1
exit /b 0

:move_folder
set "TRY=1"
:move_folder_try
if exist "%TARGET%" goto :move_folder_exists
if %TRY% geq 10 goto :move_folder_last
ver > nul
move "%FOLDER%" "%TARGET%" > nul 2> nul
if not errorlevel 1 if not exist "%FOLDER%" exit /b 0
call :wait_folder
goto :move_folder_try
:move_folder_last
ver > nul
move "%FOLDER%" "%TARGET%" > nul
if errorlevel 1 exit /b 1
if exist "%FOLDER%" exit /b 1
exit /b 0
:move_folder_exists
echo The following folder already exists, so the folder was not moved into its place:
for %%P in ("%TARGET%") do echo(    %%~P
exit /b 1

:delete_folder
set "TRY=1"
:delete_folder_try
if %TRY% geq 10 goto :delete_folder_last
rmdir /S /Q "%FOLDER%" 2> nul
if not exist "%FOLDER%" exit /b 0
call :wait_folder
goto :delete_folder_try
:delete_folder_last
rmdir /S /Q "%FOLDER%"
if exist "%FOLDER%" exit /b 1
exit /b 0

:wait_folder
if %TRY% equ 1 echo Another program is using a file in the folder. Waiting for it to finish (up to about 10 seconds)...
set /a TRY+=1
ping -n 2 127.0.0.1 > nul
exit /b 0
rem ---- end of the folder operations
