@echo off
rem Builds the layout search with the C# compiler that ships with Windows'
rem .NET Framework 4.x: nothing to install. Output: build\layout\LayoutOpt.exe
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set OUT=%~dp0..\..\build\layout
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /target:exe /optimize+ /out:"%OUT%\LayoutOpt.exe" ^
	"%~dp0LayoutOpt.cs"
