@echo off
setlocal

set "EXE=%~dp0TableNav.exe"

if not exist "%EXE%" (
    echo TableNav.exe not found. Please run build.bat first.
    pause
    exit /b 1
)

echo Registering TableNav as an "Open With" option for .csv files ...

:: Register the application
reg add "HKCU\Software\Classes\Applications\TableNav.exe"                              /ve /d "TableNav"                                   /f >nul
reg add "HKCU\Software\Classes\Applications\TableNav.exe"                              /v "FriendlyAppName" /d "TableNav"                  /f >nul
reg add "HKCU\Software\Classes\Applications\TableNav.exe\DefaultIcon"                  /ve /d "\"%EXE%\",0"                              /f >nul
reg add "HKCU\Software\Classes\Applications\TableNav.exe\shell\open\command"           /ve /d "\"%EXE%\" \"%%1\""                        /f >nul
reg add "HKCU\Software\Classes\Applications\TableNav.exe\SupportedTypes"               /v ".csv"  /d "" /f >nul
reg add "HKCU\Software\Classes\Applications\TableNav.exe\SupportedTypes"               /v ".tsv"  /d "" /f >nul
reg add "HKCU\Software\Classes\Applications\TableNav.exe\SupportedTypes"               /v ".txt"  /d "" /f >nul
reg add "HKCU\Software\Classes\Applications\TableNav.exe\SupportedTypes"               /v ".xlsx" /d "" /f >nul

:: Add to Open With list for .csv and .xlsx files
reg add "HKCU\Software\Classes\.csv\OpenWithList\TableNav.exe"                         /ve /f >nul
reg add "HKCU\Software\Classes\.xlsx\OpenWithList\TableNav.exe"                        /ve /f >nul

:: Flush the Windows shell icon cache (delete the db files while Explorer is briefly stopped)
taskkill /f /im explorer.exe >nul 2>&1
timeout /t 1 /nobreak >nul
del /f /q "%LocalAppData%\Microsoft\Windows\Explorer\iconcache*.db"  2>nul
del /f /q "%LocalAppData%\Microsoft\Windows\Explorer\thumbcache*.db" 2>nul
start "" explorer.exe
timeout /t 1 /nobreak >nul

echo.
echo Done!
echo.
echo Right-click any .csv or .xlsx file ^> "Open with" ^> "Choose another app"
echo Scroll down to find TableNav, or check "Always use this app".
echo.
pause
