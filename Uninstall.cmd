@echo off
rem Removes the UrlIntercepter registration. Set HTTP/HTTPS back to your browser in Default apps first.

reg delete "HKCU\Software\RegisteredApplications" /v "URL Intercepter" /f >nul 2>&1
reg delete "HKCU\Software\UrlIntercepter" /f >nul 2>&1
reg delete "HKCU\Software\Classes\UrlIntercepter.Link" /f >nul 2>&1

echo URL Intercepter unregistered.
pause
