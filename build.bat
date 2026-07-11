@echo off
rem build script for wechat-daemon

echo building windows wechat backend...
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /main:WeChatSidekick.Backend.BackendProgram /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /out:wechat-backend.exe src\Backend\*.cs src\Shared\*.cs

if %errorlevel% neq 0 (
    echo backend build failed!
    exit /b %errorlevel%
)

echo build succeeded!

