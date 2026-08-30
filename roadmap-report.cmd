@echo off
rem The backlog split by kind. See tools\roadmap-report.ps1 for what the three kinds are and why
rem one number cannot answer whether the plan is converging.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\roadmap-report.ps1" %*
