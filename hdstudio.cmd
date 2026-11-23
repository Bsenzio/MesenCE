@echo off
rem HD Studio launcher: runs Mesen in HD Studio mode and waits until it finishes (see HDSTUDIO.md)
rem Example: hdstudio.cmd "D:\ROMs\game.nes" UI\HdStudio\Scripts\explore.lua --frames=18000
rem Set MESEN_HOME to keep settings/HdPacks outside Documents (see HDSTUDIO.md)
set "MESEN=%~dp0bin\win-x64\Release\Mesen.exe"
if not exist "%MESEN%" (
	echo Mesen.exe not found in "%~dp0bin\win-x64\Release". Build the solution first.
	exit /b 1
)
"%MESEN%" --hdstudio %*
exit /b %ERRORLEVEL%
