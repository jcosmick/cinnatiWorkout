@echo off
rem Aggiorna la versione portable e l'installer MSI in .\dist
setlocal
cd /d "%~dp0"

echo === Versione portable ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-portable.ps1"
if errorlevel 1 goto :error

echo.
echo === Installer MSI ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1"
if errorlevel 1 goto :error

echo.
echo Fatto: portable e installer aggiornati in "%~dp0dist"
pause
exit /b 0

:error
echo.
echo ERRORE: creazione non riuscita. Se Cinnati Workout e' aperto dalla cartella build, chiudilo e riprova.
pause
exit /b 1
