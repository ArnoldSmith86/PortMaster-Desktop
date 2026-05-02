; PortMaster Desktop — Windows Installer
; Build with: makensis windows-installer.nsi
; Requires: NSIS 3.x
; Variables passed by the CI build script:
;   /DAPP_VERSION=x.y.z
;   /DEXE_SRC=path\to\PortMasterDesktop.exe
;   /DICON_SRC=path\to\portmaster.ico

!ifndef APP_VERSION
  !define APP_VERSION "0.0.0"
!endif

!define APP_NAME      "PortMaster Desktop"
!define APP_SLUG      "PortMasterDesktop"
!define PUBLISHER     "PortMaster"
!define INSTALL_DIR   "$PROGRAMFILES64\${APP_NAME}"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_SLUG}"

Name        "${APP_NAME} ${APP_VERSION}"
OutFile     "../PortMasterDesktop-${APP_VERSION}-windows-x64-setup.exe"
InstallDir  "${INSTALL_DIR}"
InstallDirRegKey HKLM "${UNINSTALL_KEY}" "InstallLocation"

RequestExecutionLevel admin

SetCompressor /SOLID lzma

; Modern UI
!include "MUI2.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON   "${ICON_SRC}"
!define MUI_UNICON "${ICON_SRC}"

; Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ── Install ──────────────────────────────────────────────────────────────────

Section "PortMaster Desktop" SecMain
  SectionIn RO   ; required section

  SetOutPath "$INSTDIR"
  File "${EXE_SRC}"

  ; Start Menu shortcut
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut  "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" \
                  "$INSTDIR\PortMasterDesktop.exe" "" "$INSTDIR\PortMasterDesktop.exe" 0
  CreateShortcut  "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" \
                  "$INSTDIR\Uninstall.exe"

  ; Write uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Add/Remove Programs entry
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayName"      "${APP_NAME}"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayVersion"   "${APP_VERSION}"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "Publisher"        "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "InstallLocation"  "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "UninstallString"  '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINSTALL_KEY}" "DisplayIcon"      "$INSTDIR\PortMasterDesktop.exe"
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify"         1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair"         1
SectionEnd

; ── Uninstall ─────────────────────────────────────────────────────────────────

Section "Uninstall"
  Delete "$INSTDIR\PortMasterDesktop.exe"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"

  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${APP_NAME}"

  DeleteRegKey HKLM "${UNINSTALL_KEY}"
SectionEnd
