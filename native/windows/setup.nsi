Unicode true
RequestExecutionLevel user
Name "BUAA Learn Native"
OutFile "${SETUP_OUT}"
InstallDir "$LOCALAPPDATA\Programs\BUAA Learn Native"
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!define PRODUCT_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\BUAALearnNative"
!define WEBVIEW_KEY "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
!define WEBVIEW_URL "https://developer.microsoft.com/microsoft-edge/webview2/#download-section"

!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "BUAA Learn Native requires 64-bit Windows 11." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ${If} ${AtLeastWin10}
    SetRegView 64
    ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
    IntCmp $0 22000 supported unsupported supported
  ${EndIf}
  unsupported:
    MessageBox MB_OK|MB_ICONSTOP "BUAA Learn Native requires Windows 11." /SD IDOK
    SetErrorLevel 2
    Abort
  supported:
    ${DisableX64FSRedirection}
    ; Do not accept an overridden destination.
    StrCpy $INSTDIR "$LOCALAPPDATA\Programs\BUAA Learn Native"
    SetRegView 32
    ReadRegStr $0 HKLM "${WEBVIEW_KEY}" "pv"
    ${If} $0 == ""
    ${OrIf} $0 == "0.0.0.0"
      SetRegView 64
      ReadRegStr $0 HKCU "${WEBVIEW_KEY}" "pv"
    ${EndIf}
    ${If} $0 == ""
    ${OrIf} $0 == "0.0.0.0"
      SetRegView 32
      ReadRegStr $0 HKCU "${WEBVIEW_KEY}" "pv"
    ${EndIf}
    ${If} $0 == ""
    ${OrIf} $0 == "0.0.0.0"
      MessageBox MB_YESNO|MB_ICONEXCLAMATION "Install Microsoft Edge WebView2 Runtime first. Open the Microsoft download page?" /SD IDNO IDNO noWebView
      ExecShell "open" "${WEBVIEW_URL}"
      noWebView:
      SetErrorLevel 3
      Abort
    ${EndIf}
    SetRegView 64
FunctionEnd

Section
  SetShellVarContext current
  SetOutPath "$INSTDIR\versions\${APP_VERSION}-${BUILD_ID}"
  ClearErrors
  File /r "${PAYLOAD_DIR}\*"
  IfErrors failed
  IfFileExists "$OUTDIR\BUAALearn.Probe.exe" 0 failed
  IfFileExists "$OUTDIR\buaa_core.dll" 0 failed
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors failed
  CreateDirectory "$SMPROGRAMS\BUAA Learn Native"
  CreateShortCut "$SMPROGRAMS\BUAA Learn Native\BUAA Learn Native.lnk" "$OUTDIR\BUAALearn.Probe.exe"
  CreateShortCut "$SMPROGRAMS\BUAA Learn Native\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayName" "BUAA Learn Native"
  WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${PRODUCT_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${PRODUCT_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayIcon" "$OUTDIR\BUAALearn.Probe.exe"
  WriteRegStr HKCU "${PRODUCT_KEY}" "AppExecutable" "$OUTDIR\BUAALearn.Probe.exe"
  WriteRegDWORD HKCU "${PRODUCT_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${PRODUCT_KEY}" "NoRepair" 1
  Goto done
  failed:
    MessageBox MB_OK|MB_ICONSTOP "Installation could not finish. Close BUAA Learn and try again." /SD IDOK
    SetErrorLevel 4
    Abort
  done:
SectionEnd

Function un.onInit
  ${DisableX64FSRedirection}
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\BUAA Learn Native"
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\BUAALearnNativeRunning") p.r1'
  IntCmp $1 0 closed
    System::Call 'kernel32::CloseHandle(p r1)'
    MessageBox MB_OK|MB_ICONSTOP "Close BUAA Learn Native before uninstalling." /SD IDOK
    SetErrorLevel 4
    Abort
  closed:
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  ClearErrors
  RMDir /r "$INSTDIR\versions"
  IfErrors locked
  Delete "$SMPROGRAMS\BUAA Learn Native\BUAA Learn Native.lnk"
  Delete "$SMPROGRAMS\BUAA Learn Native\Uninstall.lnk"
  RMDir "$SMPROGRAMS\BUAA Learn Native"
  DeleteRegKey HKCU "${PRODUCT_KEY}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Goto end
  locked:
    SetErrorLevel 4
    Abort
  end:
SectionEnd
