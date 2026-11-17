@echo off
rem Publishes nova-astro.app: ..\website, with the screenshots from ..\docs\images as img\.
rem Needs Node.js; wrangler asks you to sign in to Cloudflare once.
rem "deploy.bat build" only fills public\ (to look at it locally) without publishing.
setlocal
cd /d "%~dp0"
if exist public rmdir /s /q public
robocopy ..\website public /e >nul
if errorlevel 8 exit /b 1
robocopy ..\docs\images public\img *.webp >nul
if errorlevel 8 exit /b 1
if /i "%1"=="build" exit /b 0
call npx --yes wrangler@4 deploy || exit /b 1
echo Live at https://nova-astro.app
