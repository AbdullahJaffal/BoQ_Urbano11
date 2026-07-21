@echo off
REM Usage: build.bat [suffix]   example: build.bat 112
REM Pass a fresh suffix when a running AutoCAD locks the previous output DLL.
REM Do not remove the Platform x64 build flag below; without it the build no-ops.
setlocal
set "SUFFIX=%~1"
if "%SUFFIX%"=="" set "SUFFIX=Local"
set "LOG=%~dp0build_v%SUFFIX%_out.txt"
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" "%~dp0UrbanoMetraj.csproj" /p:Configuration=Debug /p:OutputPath=bin\DebugV%SUFFIX%\ /p:Platform=x64 /t:Build /v:minimal > "%LOG%" 2>&1
echo EXIT_CODE=%ERRORLEVEL% >> "%LOG%"
type "%LOG%"
endlocal
