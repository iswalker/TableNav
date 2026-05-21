@echo off
echo Removing TableNav registry entries ...
reg delete "HKCU\Software\Classes\Applications\TableNav.exe"       /f >nul 2>&1
reg delete "HKCU\Software\Classes\.csv\OpenWithList\TableNav.exe"  /f >nul 2>&1
echo Done. TableNav removed from "Open With" lists.
pause
