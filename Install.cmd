@echo off
rem Builds UrlIntercepter.exe from this folder and registers it as a link handler (current user only, no admin).
rem Run it again after editing the .cs files or moving the folder.

set "SOURCE=%~dp0UrlIntercepter.cs"
set "UI=%~dp0FluentUI.cs"
set "EXE=%~dp0UrlIntercepter.exe"
for %%F in ("%SOURCE%" "%UI%") do if not exist %%F (
    echo %%~nxF not found next to this file.
    pause
    exit /b 1
)

rem C# compiler that ships with Windows (.NET Framework 4)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo The .NET Framework 4 C# compiler was not found.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:"%EXE%" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "%SOURCE%" "%UI%"
if errorlevel 1 (
    echo.
    echo Build failed. If a link window is open, close it and try again.
    pause
    exit /b 1
)

reg add "HKCU\Software\Classes\UrlIntercepter.Link" /ve /d "URL Intercepter" /f >nul
reg add "HKCU\Software\Classes\UrlIntercepter.Link\shell\open\command" /ve /d "\"%EXE%\" \"%%1\"" /f >nul
reg add "HKCU\Software\UrlIntercepter\Capabilities" /v ApplicationName /d "URL Intercepter" /f >nul
reg add "HKCU\Software\UrlIntercepter\Capabilities" /v ApplicationDescription /d "Review links before opening them" /f >nul
reg add "HKCU\Software\UrlIntercepter\Capabilities\URLAssociations" /v http /d "UrlIntercepter.Link" /f >nul
reg add "HKCU\Software\UrlIntercepter\Capabilities\URLAssociations" /v https /d "UrlIntercepter.Link" /f >nul
reg add "HKCU\Software\RegisteredApplications" /v "URL Intercepter" /d "Software\UrlIntercepter\Capabilities" /f >nul

echo Registered: %EXE%
echo.
echo If links still open directly in the browser, open Settings ^> Apps ^> Default apps ^> "URL Intercepter" and set HTTP and HTTPS to it.
pause
