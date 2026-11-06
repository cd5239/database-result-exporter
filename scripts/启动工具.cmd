@echo off
setlocal
set "APP=%~dp0runtime\DatabaseResultExporter.exe"
if not exist "%APP%" (
    echo Application files are missing. Extract the complete ZIP and try again.
    pause
    exit /b 1
)
if "%~1"=="" (
    start "" "%APP%"
) else (
    "%APP%" %*
)
exit /b %errorlevel%
