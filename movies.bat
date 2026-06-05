@echo off
cd /d "%~dp0"
wt --maximized -d "%CD%" cmd /c "title MovieCLI && dotnet run .\Program.cs"