@echo off
rem Builds the Guide with the C# compiler that ships with Windows' .NET
rem Framework 4.x: nothing to install. Output: build\guide\LizardKingGuide.exe
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set OUT=%~dp0..\build\guide
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
	/out:"%OUT%\LizardKingGuide.exe" ^
	/r:System.Windows.Forms.dll /r:System.Drawing.dll ^
	"%~dp0LizardKingGuide.cs"
