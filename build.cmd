@echo off
rem Builds bin\MiniStats.exe with the C# compiler that ships with .NET Framework 4.x (no SDK required).
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [build] .NET Framework 4.x compiler not found: %CSC%
  exit /b 1
)
if not exist bin mkdir bin
"%CSC%" -nologo -codepage:65001 -target:winexe -optimize+ -platform:x64 ^
  -win32icon:assets\icon.ico -resource:assets\tray.ico,MiniStats.tray.ico ^
  -out:bin\MiniStats.exe src\MiniStats.cs src\AssemblyInfo.cs
if errorlevel 1 exit /b 1
echo [build] bin\MiniStats.exe
