@echo off
rem Startet SKx Solar auf dem PC, nur von diesem Rechner aus erreichbar.
cd /d "%~dp0"
start "" http://localhost:8080
SKx.Solar.exe --Urls=http://localhost:8080
