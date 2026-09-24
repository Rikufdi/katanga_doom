@echo off
rem Builds openvr_api.dll (x64) from an "x64 Native Tools Command Prompt for VS".
cd /d "%~dp0"
cl /nologo /LD /O2 /EHsc openvr_stub.cpp /Fe:openvr_api.dll /link /DEF:openvr_stub.def
if errorlevel 1 exit /b 1
del /q openvr_stub.obj openvr_api.lib openvr_api.exp 2>nul
echo Built %~dp0openvr_api.dll
