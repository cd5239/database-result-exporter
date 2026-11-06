@echo off
setlocal
cd /d "%~dp0"
dotnet publish DatabaseResultExporter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o ..\publish
exit /b %errorlevel%
