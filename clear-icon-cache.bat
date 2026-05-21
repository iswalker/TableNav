@echo off
echo Clearing Windows shell icon cache...
echo (Explorer will briefly disappear and restart)
echo.

:: Kill Explorer — desktop/taskbar will go blank for a second
taskkill /f /im explorer.exe >nul 2>&1
timeout /t 1 /nobreak >nul

:: Delete the icon cache database files while Explorer is stopped
del /f /q "%LocalAppData%\Microsoft\Windows\Explorer\iconcache*.db"  2>nul
del /f /q "%LocalAppData%\Microsoft\Windows\Explorer\thumbcache*.db" 2>nul

:: Restart Explorer
start "" explorer.exe

echo Done! Open a new Explorer window and the updated icon should appear.
echo.
timeout /t 3 /nobreak >nul
