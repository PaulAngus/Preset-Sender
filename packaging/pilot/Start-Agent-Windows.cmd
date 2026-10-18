@echo off
cd /d "%~dp0"
"%~dp0bin\lab\PresetSender.Lab.exe" agent --root "%~dp0." %*
set "PILOT_EXIT=%ERRORLEVEL%"
if not defined PRESET_SENDER_NO_PAUSE pause
exit /b %PILOT_EXIT%
