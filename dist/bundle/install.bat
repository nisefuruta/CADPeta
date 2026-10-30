@echo off
rem CADPeta installer for AutoCAD (no administrator rights needed).
rem Installs CADPeta.bundle into %APPDATA%\Autodesk\ApplicationPlugins.
rem This file is ASCII only, so it does not depend on the console code page. All messages are English.
rem A path is shown on its own line by a for loop over the quoted path,
rem so that special characters in the path (ampersand, caret, exclamation mark, parentheses) are not parsed.
rem Japanese guide: see README-install.txt.
rem Steps: 0 check that the zip was extracted completely (bundle-files.txt), 1 check that no installed DLL is open,
rem 2 clean up leftovers of an interrupted run, 3 copy the new version to CADPeta.bundle.new (outside ApplicationPlugins),
rem 4 rename the installed folder to CADPeta.bundle.old, 5 move the new folder to CADPeta.bundle, 6 delete CADPeta.bundle.old.
rem If step 5 fails, the incomplete CADPeta.bundle is deleted and the previous install is restored.
rem After installation, the PETA command is available the next time AutoCAD starts.
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
set "SRC=%~dp0CADPeta.bundle"
set "LIST=%~dp0bundle-files.txt"

if not exist "%SRC%\PackageContents.xml" goto :no_source

rem Step 0: check that no file was lost when the zip was extracted. bundle-files.txt lists every file of CADPeta.bundle
rem and is made when the distribution is built. If a file is missing, stop without changing anything.
if not exist "%LIST%" goto :no_list
set "LISTED="
set "MISSING="
for /f "usebackq delims=" %%L in ("%LIST%") do (
    set "LISTED=1"
    if not exist "%SRC%\%%L" if not defined MISSING set "MISSING=%%L"
)
if not defined LISTED goto :no_list
if defined MISSING goto :missing_file

echo Installing CADPeta to:
for %%P in ("%DEST%") do echo(    %%~P

rem Step 1: nothing is changed until this check passes.
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

rem Step 2: delete leftovers of an interrupted run.
rem   - CADPeta.bundle.new is a partial copy: delete it.
rem   - CADPeta.bundle.old with PackageContents.xml and no CADPeta.bundle is the previous install: restore it.
rem   - CADPeta.bundle.old without PackageContents.xml is a half-deleted uninstall: delete it, do not restore it.
rem   - CADPeta.bundle.old next to a CADPeta.bundle is an old backup: delete it.
if not exist "%NEW%" goto :new_checked
echo Deleting a partial copy left by an earlier install...
set "FOLDER=%NEW%"
call :delete_folder
if errorlevel 1 goto :new_stale_failed

:new_checked
if not exist "%OLD%" goto :old_checked
if exist "%DEST%" goto :delete_stale_old
if not exist "%OLD%\PackageContents.xml" goto :delete_stale_old
echo Restoring the earlier installation left by an interrupted install...
set "FOLDER=%OLD%"
set "NEWNAME=%DESTNAME%"
call :rename_folder
if errorlevel 1 goto :restore_failed
goto :old_checked

:delete_stale_old
echo Deleting a leftover backup from an earlier install...
set "FOLDER=%OLD%"
call :delete_folder
if errorlevel 1 goto :old_stale_failed

:old_checked

rem Step 3: copy the new version to CADPeta.bundle.new. The installed folder is not touched.
rem robocopy is used because xcopy fails ("Insufficient memory") when a file path is 260 characters or longer
rem (the zip extracted into a deep folder, or a long APPDATA). robocopy exit codes 0 to 7 mean success, 8 or more mean failure.
rem /R:2 /W:1 limits the retries of a file that cannot be copied (the default is 1,000,000 retries, 30 seconds apart).
ver > nul
robocopy "%SRC%" "%NEW%" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul
if errorlevel 8 goto :copy_failed
if not exist "%NEW%\PackageContents.xml" goto :copy_failed

rem Step 4: rename the installed folder to CADPeta.bundle.old. This fails while AutoCAD has the plug-in loaded; nothing has changed then.
if not exist "%DEST%" goto :move_new
echo Renaming the existing installation to CADPeta.bundle.old...
set "FOLDER=%DEST%"
set "NEWNAME=%OLDNAME%"
call :rename_folder
if errorlevel 1 goto :in_use

rem Step 5: move the new version to the install folder.
rem The error message of mkdir (standard error) is not hidden, so that the reason given by Windows is shown as it is.
:move_new
if not exist "%PARENT%" mkdir "%PARENT%"
if not exist "%PARENT%" goto :parent_failed
set "FOLDER=%NEW%"
set "TARGET=%DEST%"
call :move_folder
if errorlevel 1 goto :move_failed
if not exist "%DEST%\PackageContents.xml" goto :move_failed

rem Step 6: the backup is not needed any more.
if not exist "%OLD%" goto :done
set "FOLDER=%OLD%"
call :delete_folder
if errorlevel 1 goto :old_left

:done
echo.
echo [DONE] Install succeeded. Start AutoCAD and type PETA at the command line.
echo        If AutoCAD is already running, restart it to load CADPeta.
echo        If CADPeta is not loaded, see README-install.txt.
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 0

:no_source
echo [ERROR] CADPeta.bundle not found next to this script:
for %%P in ("%SRC%") do echo(    %%~P
echo         Extract all files of the zip, then run this again. Nothing was changed.
goto :failed

:no_list
echo [ERROR] The file list bundle-files.txt is missing or empty:
for %%P in ("%LIST%") do echo(    %%~P
echo         Extract all files of the zip, then run this again. Nothing was changed.
goto :failed

:missing_file
echo [ERROR] A file of the zip was not extracted:
for %%P in ("%SRC%\%MISSING%") do echo(    %%~P
echo         Extract all files of the zip, then run this again. Nothing was changed.
goto :failed

:in_use_file
echo [ERROR] An installed CADPeta file cannot be opened:
for %%P in ("%INUSE%") do echo(    %%~P
echo         AutoCAD is probably running with CADPeta loaded, or the file is read-only.
echo         Close AutoCAD and run this again. The installation was not changed.
goto :failed

:new_stale_failed
echo [ERROR] Could not delete the partial copy left by an earlier install:
for %%P in ("%NEW%") do echo(    %%~P
echo         Close AutoCAD, delete that folder, and run this again. The installation was not changed.
goto :failed

:old_stale_failed
echo [ERROR] Could not delete the leftover backup from an earlier install:
for %%P in ("%OLD%") do echo(    %%~P
echo         Close AutoCAD, delete that folder, and run this again. The installation was not changed.
goto :failed

:restore_failed
echo [ERROR] Could not restore the earlier installation.
echo         The earlier version is in this folder:
for %%P in ("%OLD%") do echo(    %%~P
echo         Close AutoCAD, rename that folder to CADPeta.bundle, and run this again.
goto :failed

:copy_failed
echo [ERROR] Copying the new version failed. The existing installation was not changed.
call :discard_new
goto :failed

:in_use
echo [ERROR] Could not rename the existing installation. AutoCAD with CADPeta loaded, or another program, is using a file in it.
echo         Close AutoCAD and run this again. The installation was not changed.
call :discard_new
goto :failed

:parent_failed
echo [ERROR] Could not create the folder for AutoCAD plug-ins:
for %%P in ("%PARENT%") do echo(    %%~P
echo         The reason given by Windows is shown above. The installation was not changed.
call :discard_new
goto :failed

:move_failed
echo [ERROR] Could not move the new version into place.
call :discard_new
if not exist "%DEST%" goto :dest_clear
set "FOLDER=%DEST%"
call :delete_folder
if errorlevel 1 goto :partial_left

:dest_clear
if not exist "%OLD%" goto :nothing_installed
set "FOLDER=%OLD%"
set "NEWNAME=%DESTNAME%"
call :rename_folder
if errorlevel 1 goto :restore_failed
echo         The earlier installation was restored.
goto :failed

:nothing_installed
echo         CADPeta is not installed. Run this again to install it.
goto :failed

:partial_left
echo         The incomplete folder could not be deleted:
for %%P in ("%DEST%") do echo(    %%~P
if not exist "%OLD%" goto :partial_left_no_old
echo         Close AutoCAD and delete that folder.
echo         Then rename the following folder to CADPeta.bundle to use the earlier version:
for %%P in ("%OLD%") do echo(    %%~P
goto :failed

:partial_left_no_old
echo         Close AutoCAD, delete that folder, and run this again.
goto :failed

:old_left
echo [ERROR] The new version is installed, but the folder of the earlier version could not be deleted:
for %%P in ("%OLD%") do echo(    %%~P
echo         Close AutoCAD and delete that folder yourself.
goto :failed

:failed
echo.
echo [FAILED] Install did not complete. See the message above.
echo          Japanese guide: README-install.txt
echo.
echo Press any key to close this window.
pause > nul
endlocal
exit /b 1

rem :discard_new deletes the partial copy. If it cannot, the folder is shown so that the user can delete it.
:discard_new
if not exist "%NEW%" exit /b 0
set "FOLDER=%NEW%"
call :delete_folder
if not errorlevel 1 exit /b 0
echo         The following folder could not be deleted. Delete it yourself:
for %%P in ("%NEW%") do echo(    %%~P
exit /b 0

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
