@echo off
setlocal
"%~dp0bin\lab\PresetSender.Lab.exe" run --root "%~dp0." %*
set "pilot_exit=%errorlevel%"
if not defined PRESET_SENDER_NO_PAUSE pause
exit /b %pilot_exit%
