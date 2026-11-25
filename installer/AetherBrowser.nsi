; Aether Browser — Native Windows NSIS MUI2 Installer
; Clean, classic Windows Setup Wizard (per-user & all-users, native progress bar, silent update support)

Unicode True

!ifndef APP_VERSION
  !define APP_VERSION "2.2.0"
!endif
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\dist\publish-v2.2.0"
!endif
!ifndef OUT_FILE
  !define OUT_FILE "..\dist\releases\AetherBrowser-Setup.exe"
!endif

!define APP_NAME "Aether Browser"
!define APP_PUBLISHER "Aether"
!define APP_EXE "AetherBrowser.exe"
!define APP_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AetherBrowser"
!define APP_CLIENT_KEY "Software\Clients\StartMenuInternet\AetherBrowser"
!define APP_PROG_ID "AetherHTML"

Name "${APP_NAME}"
Caption "${APP_NAME} Setup"
OutFile "${OUT_FILE}"
BrandingText "${APP_NAME} ${APP_VERSION}"
InstallDir "$LOCALAPPDATA\AetherBrowser"
RequestExecutionLevel user
SetCompressor zlib
ManifestDPIAware true

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Open Source"

!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

; --- MUI2 Classic Clean Appearance ---
!define MUI_ICON "..\src\AetherBrowser\Assets\aether.ico"
!define MUI_UNICON "..\src\AetherBrowser\Assets\aether.ico"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "assets\header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "assets\header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP "assets\sidebar.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "assets\sidebar.bmp"
!define MUI_ABORTWARNING

!define MUI_FINISHPAGE_TITLE "Completing ${APP_NAME} Setup"
!define MUI_FINISHPAGE_TEXT "${APP_NAME} has been installed on your computer.$\r$\n$\r$\nClick Finish to close Setup."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Run ${APP_NAME}"

; --- Pages (Exact classic Windows NSIS flow) ---
Page custom InstallModePageCreate InstallModePageLeave
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; --- Variables ---
Var InstallScopeAllUsers
Var RadioAllUsers
Var RadioCurrentUser
Var CurrentWinUser
Var IsAdminUser

Function .onInit
  StrCpy $InstallScopeAllUsers "0"

  ; Read current Windows username
  System::Call "advapi32::GetUserNameW(t .r0, *i 256) i"
  StrCpy $CurrentWinUser $0
  ${If} $CurrentWinUser == ""
    StrCpy $CurrentWinUser "current user"
  ${EndIf}

  ; Check whether running elevated as Admin
  UserInfo::GetAccountType
  Pop $1
  ${If} $1 == "Admin"
    StrCpy $IsAdminUser "1"
  ${Else}
    StrCpy $IsAdminUser "0"
  ${EndIf}

  ; Reuse existing install folder if already installed (unless /D= passed)
  ${If} $INSTDIR == "$LOCALAPPDATA\AetherBrowser"
    ReadRegStr $2 HKCU "${APP_UNINST_KEY}" "InstallLocation"
    ${If} $2 != ""
      StrCpy $INSTDIR $2
    ${EndIf}
  ${EndIf}
FunctionEnd

Function InstallModePageCreate
  !insertmacro MUI_HEADER_TEXT "Choose Install Options" "Who should this application be installed for?"

  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0u 6u 100% 24u "Please select whether you wish to make this software available to all users or just yourself."
  Pop $1

  ${NSD_CreateRadioButton} 12u 42u 95% 14u "Anyone who uses this computer (all users) (must run as admin)"
  Pop $RadioAllUsers
  ${If} $IsAdminUser != "1"
    EnableWindow $RadioAllUsers 0
  ${EndIf}

  ${NSD_CreateRadioButton} 12u 62u 95% 14u "Only for me ($CurrentWinUser)"
  Pop $RadioCurrentUser

  ${If} $InstallScopeAllUsers == "1"
    ${NSD_Check} $RadioAllUsers
  ${Else}
    ${NSD_Check} $RadioCurrentUser
  ${EndIf}

  nsDialogs::Show
FunctionEnd

Function InstallModePageLeave
  ${NSD_GetState} $RadioAllUsers $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $InstallScopeAllUsers "1"
    SetShellVarContext all
    ${If} $INSTDIR == "$LOCALAPPDATA\AetherBrowser"
      StrCpy $INSTDIR "$PROGRAMFILES64\${APP_NAME}"
    ${EndIf}
  ${Else}
    StrCpy $InstallScopeAllUsers "0"
    SetShellVarContext current
    ${If} $INSTDIR == "$PROGRAMFILES64\${APP_NAME}"
      StrCpy $INSTDIR "$LOCALAPPDATA\AetherBrowser"
    ${EndIf}
  ${EndIf}
FunctionEnd

Section "Install Aether Browser" SecMain
  SectionIn RO

  ${If} $InstallScopeAllUsers == "1"
    SetShellVarContext all
  ${Else}
    SetShellVarContext current
  ${EndIf}

  ; Gracefully close any running instance before updating files
  nsExec::ExecToStack 'taskkill /IM "${APP_EXE}" /T'
  Sleep 450
  nsExec::ExecToStack 'taskkill /F /IM "${APP_EXE}" /T'
  Sleep 250

  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*.*"

  ; Write native uninstaller
  WriteUninstaller "$INSTDIR\Uninstall ${APP_NAME}.exe"

  ; Shortcuts
  CreateShortCut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  ; Compute installed size in KB for Windows Installed Apps
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0

  ; Register in Windows Settings -> Installed Apps
  ${If} $InstallScopeAllUsers == "1"
    WriteRegStr HKLM "${APP_UNINST_KEY}" "DisplayName" "${APP_NAME}"
    WriteRegStr HKLM "${APP_UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKLM "${APP_UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
    WriteRegStr HKLM "${APP_UNINST_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${APP_UNINST_KEY}" "DisplayIcon" '"$INSTDIR\${APP_EXE}",0'
    WriteRegStr HKLM "${APP_UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall ${APP_NAME}.exe"'
    WriteRegStr HKLM "${APP_UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall ${APP_NAME}.exe" /S'
    WriteRegDWORD HKLM "${APP_UNINST_KEY}" "EstimatedSize" "$0"
    WriteRegDWORD HKLM "${APP_UNINST_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${APP_UNINST_KEY}" "NoRepair" 1
  ${Else}
    WriteRegStr HKCU "${APP_UNINST_KEY}" "DisplayName" "${APP_NAME}"
    WriteRegStr HKCU "${APP_UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${APP_UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
    WriteRegStr HKCU "${APP_UNINST_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${APP_UNINST_KEY}" "DisplayIcon" '"$INSTDIR\${APP_EXE}",0'
    WriteRegStr HKCU "${APP_UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall ${APP_NAME}.exe"'
    WriteRegStr HKCU "${APP_UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall ${APP_NAME}.exe" /S'
    WriteRegDWORD HKCU "${APP_UNINST_KEY}" "EstimatedSize" "$0"
    WriteRegDWORD HKCU "${APP_UNINST_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${APP_UNINST_KEY}" "NoRepair" 1
  ${EndIf}

  ; Register Windows Default Browser capabilities (HKCU — works cleanly without UAC)
  WriteRegStr HKCU "Software\Classes\${APP_PROG_ID}" "" "${APP_NAME} HTML Document"
  WriteRegStr HKCU "Software\Classes\${APP_PROG_ID}" "URL Protocol" ""
  WriteRegStr HKCU "Software\Classes\${APP_PROG_ID}\DefaultIcon" "" '"$INSTDIR\${APP_EXE}",0'
  WriteRegStr HKCU "Software\Classes\${APP_PROG_ID}\shell\open\command" "" '"$INSTDIR\${APP_EXE}" "%1"'

  WriteRegStr HKCU "${APP_CLIENT_KEY}" "" "${APP_NAME}"
  WriteRegStr HKCU "${APP_CLIENT_KEY}\DefaultIcon" "" '"$INSTDIR\${APP_EXE}",0'
  WriteRegStr HKCU "${APP_CLIENT_KEY}\shell\open\command" "" '"$INSTDIR\${APP_EXE}"'
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities" "ApplicationName" "${APP_NAME}"
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities" "ApplicationDescription" "Ultra-lightweight, privacy-first, open-source web browser."
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities" "ApplicationIcon" '"$INSTDIR\${APP_EXE}",0'
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities\URLAssociations" "http" "${APP_PROG_ID}"
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities\URLAssociations" "https" "${APP_PROG_ID}"
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities\FileAssociations" ".htm" "${APP_PROG_ID}"
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities\FileAssociations" ".html" "${APP_PROG_ID}"
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities\FileAssociations" ".webp" "${APP_PROG_ID}"
  WriteRegStr HKCU "${APP_CLIENT_KEY}\Capabilities\FileAssociations" ".svg" "${APP_PROG_ID}"
  WriteRegStr HKCU "Software\RegisteredApplications" "AetherBrowser" "${APP_CLIENT_KEY}\Capabilities"
SectionEnd

Function .onInstSuccess
  ; Auto-relaunch when invoked silently by Aether Browser's Git auto-updater (/S /RELAUNCH)
  IfSilent 0 done
    ${GetParameters} $R0
    ClearErrors
    ${GetOptions} $R0 "/RELAUNCH" $R1
    IfErrors done
      Exec '"$INSTDIR\${APP_EXE}"'
  done:
FunctionEnd

Section "Uninstall"
  nsExec::ExecToStack 'taskkill /F /IM "${APP_EXE}" /T'
  Sleep 350

  SetShellVarContext current
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}.lnk"

  SetShellVarContext all
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}.lnk"

  DeleteRegKey HKCU "${APP_UNINST_KEY}"
  DeleteRegKey HKLM "${APP_UNINST_KEY}"
  DeleteRegKey HKCU "Software\Classes\${APP_PROG_ID}"
  DeleteRegKey HKCU "${APP_CLIENT_KEY}"
  DeleteRegValue HKCU "Software\RegisteredApplications" "AetherBrowser"

  RMDir /r "$INSTDIR"
SectionEnd
