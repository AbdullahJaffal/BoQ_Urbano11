@echo off
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" "D:\Abdullah-ElsaProje\Software\UrbanoMetraj\UrbanoMetraj.csproj" /p:Configuration=Debug /p:Platform=x64 /p:OutputPath=bin\DebugV35\ /t:Build /v:minimal > D:\Abdullah-ElsaProje\Software\UrbanoMetraj\build_v7_out.txt 2>&1
echo EXIT_CODE=%ERRORLEVEL% >> D:\Abdullah-ElsaProje\Software\UrbanoMetraj\build_v7_out.txt

