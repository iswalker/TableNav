@echo off
setlocal

:: Use the 64-bit compiler if available, fall back to 32-bit
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo ERROR: csc.exe not found. .NET Framework 4 does not appear to be installed.
    pause
    exit /b 1
)

echo Building TableNav.exe ...
"%CSC%" ^
    /target:winexe ^
    /optimize ^
    /win32icon:"%~dp0Logo.ico" ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.IO.Compression.dll ^
    /reference:System.IO.Compression.FileSystem.dll ^
    /reference:System.Xml.Linq.dll ^
    /reference:"%~dp0Microsoft.Web.WebView2.Core.dll" ^
    /reference:"%~dp0Microsoft.Web.WebView2.WinForms.dll" ^
    /out:"%~dp0TableNav.exe" ^
    "%~dp0TableNav.cs"

if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED. See errors above.
    pause
    exit /b 1
)

echo.
echo Build successful!  TableNav.exe is ready.
echo Run register.bat to add "Open With" support for .csv files.
echo.
pause
