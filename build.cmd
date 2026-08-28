@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0scripts\Build-DraftingSuite.ps1" %*
