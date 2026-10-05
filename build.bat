@echo off
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /target:winexe /out:HardwareInspector.exe /r:System.Management.dll /optimize+ /platform:x64 Program.cs
if %ERRORLEVEL% equ 0 (
    echo [OK] Build successful: HardwareInspector.exe
) else (
    echo [FAIL] Build failed.
)
