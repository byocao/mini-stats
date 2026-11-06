@echo off
rem Builds bin\MiniStats.exe with the C# compiler that ships with .NET Framework 4.x (no SDK required).
rem The settings window uses WPF, which is part of the same framework (no extra download).
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
  echo [build] .NET Framework 4.x compiler not found: %CSC%
  exit /b 1
)
if not exist bin mkdir bin
"%CSC%" -nologo -codepage:65001 -target:winexe -optimize+ -platform:x64 ^
  -lib:"%FW%\WPF" ^
  -r:PresentationFramework.dll -r:PresentationCore.dll -r:WindowsBase.dll -r:System.Xaml.dll -r:WindowsFormsIntegration.dll ^
  -win32icon:assets\icon.ico ^
  -resource:assets\tray.ico,MiniStats.tray.ico ^
  -resource:src\SettingsWindow.xaml,MiniStats.SettingsWindow.xaml ^
  -out:bin\MiniStats.exe ^
  src\MiniStats.cs src\SettingsWindow.cs src\AssemblyInfo.cs
if errorlevel 1 exit /b 1
echo [build] bin\MiniStats.exe
