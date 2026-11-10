@echo off
rem Abre a Prancheta Database: sobe a API local e abre o navegador em http://127.0.0.1:5080/
rem Precisa do runtime ASP.NET Core 10 instalado (dotnet --list-runtimes).
rem Opcoes: --desenv (libera Executar), --porta=5090, --sem-navegador
title Prancheta Database - feche esta janela para encerrar
cd /d "%~dp0"
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
"%DOTNET%" "%~dp0PranchetaDatabase.dll" %*
if errorlevel 1 (
  echo.
  echo Nao foi possivel abrir. Confira se o "ASP.NET Core Runtime 10" esta instalado:
  echo https://dotnet.microsoft.com/download/dotnet/10.0
  pause
)
