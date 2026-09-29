@echo off
REM Recreate the AirYoke bridge desktop shortcut. Double-click to run.
REM Useful after moving this folder elsewhere.
REM
REM Note: batch files must use CRLF line endings and are read in the console's
REM OEM code page, so this file is kept ASCII-only on purpose. The shortcut's
REM Chinese name is built from Unicode code points instead of literal text.

setlocal
set "AY_DIR=%~dp0"
set "AY_EXE=%~dp0AirYoke.Bridge.exe"

if not exist "%AY_EXE%" (
    echo.
    echo   ERROR: AirYoke.Bridge.exe not found next to this script.
    echo   Keep this file in the same folder as the bridge program.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([IO.Path]::Combine([Environment]::GetFolderPath('Desktop'),'AirYoke '+[char]0x6865+[char]0x63A5+[char]0x7A0B+[char]0x5E8F+'.lnk'));$s.TargetPath=$env:AY_EXE;$s.WorkingDirectory=$env:AY_DIR;$s.Description='AirYoke Bridge';$s.IconLocation=$env:AY_EXE+',0';$s.Save()"

if errorlevel 1 (
    echo.
    echo   Failed to create the shortcut.
    echo.
    pause
    exit /b 1
)

echo.
echo   Desktop shortcut created.
echo.
pause
