@echo off
REM Build (if needed) and launch Model Codex (Release).
dotnet build "%~dp0src\App\ModelCodex.App.csproj" -c Release -v q
start "" "%~dp0src\App\bin\Release\net8.0-windows\ModelCodex.App.exe"
