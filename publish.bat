@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
cd /d "%~dp0"

set REGISTRY_LOCAL=192.168.11.44:5000
set REGISTRY_HUB=rsvln
set IMAGE_NAME=frte2tg

set ERRORS=0

:: -------------------------------------------
:: 1. Bump version (patch, only if sources changed)
:: -------------------------------------------
echo Bumping version...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\bump-version.ps1"
set /p VERSION=<frte2tg\version.txt
echo   Version: %VERSION%
echo.

:: -------------------------------------------
:: 2. Docker image -> local registry and Docker Hub (latest + version)
:: -------------------------------------------
echo Building Docker image...
docker build -f frte2tg\Dockerfile -t %REGISTRY_LOCAL%/%IMAGE_NAME%:latest -t %REGISTRY_LOCAL%/%IMAGE_NAME%:%VERSION% -t %REGISTRY_HUB%/%IMAGE_NAME%:latest -t %REGISTRY_HUB%/%IMAGE_NAME%:%VERSION% .
if errorlevel 1 (
    echo   FAILED: docker build
    exit /b 1
)
echo   OK: built %IMAGE_NAME%:%VERSION%
echo.

echo Pushing to %REGISTRY_LOCAL%...
docker push %REGISTRY_LOCAL%/%IMAGE_NAME%:%VERSION%
if errorlevel 1 (
    echo   FAILED: docker push %VERSION%
    set /a ERRORS+=1
)
docker push %REGISTRY_LOCAL%/%IMAGE_NAME%:latest
if errorlevel 1 (
    echo   FAILED: docker push latest
    set /a ERRORS+=1
) else (
    echo   OK: %REGISTRY_LOCAL%/%IMAGE_NAME%:latest, :%VERSION%
)

echo Pushing to Docker Hub...
docker push %REGISTRY_HUB%/%IMAGE_NAME%:%VERSION%
if errorlevel 1 (
    echo   FAILED: docker push %REGISTRY_HUB%/%IMAGE_NAME%:%VERSION% ^(run "docker login" first^)
    set /a ERRORS+=1
)
docker push %REGISTRY_HUB%/%IMAGE_NAME%:latest
if errorlevel 1 (
    echo   FAILED: docker push %REGISTRY_HUB%/%IMAGE_NAME%:latest
    set /a ERRORS+=1
) else (
    echo   OK: %REGISTRY_HUB%/%IMAGE_NAME%:latest, :%VERSION%
)

:: -------------------------------------------
:: 3. Git: commit version bump + push -> GitHub Actions builds ghcr.io image
:: -------------------------------------------
echo.
echo Pushing version to GitHub...
git add frte2tg\version.txt frte2tg\.src-hash
git diff --cached --quiet
if errorlevel 1 (
    git commit -m "chore: release v%VERSION%"
    if errorlevel 1 (
        echo   FAILED: git commit
        set /a ERRORS+=1
    ) else (
        git push origin master
        if errorlevel 1 (
            echo   FAILED: git push
            set /a ERRORS+=1
        ) else (
            echo   OK: pushed v%VERSION%
        )
    )
) else (
    echo   SKIPPED: no version changes to commit
)

echo.
if %ERRORS%==0 (
    echo Done: frte2tg v%VERSION%
) else (
    echo Finished with %ERRORS% error^(s^)
    exit /b 1
)
