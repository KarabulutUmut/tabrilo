Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WordFunc.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "${DELETE_MANIFEST}"

Name "${PRODUCT_ID}"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_ID}"
RequestExecutionLevel user
SetCompressor zlib
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "Tabrilo"
VIAddVersionKey "FileDescription" "Tabrilo Windows x64 installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Umut Karabulut and contributors"

!define PREFS "Software\${PRODUCT_ID}"
!define UNINSTALL "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"

Var Stage
Var Previous
Var VersionPath
Var InstallerMutex
Var Legacy

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
    SetShellVarContext current
    SetRegView 64
    StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${PRODUCT_ID}"
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\${PRODUCT_ID}-installer") p .r9 ?e'
    Pop $0
    StrCpy $InstallerMutex $9
    ${If} $InstallerMutex == 0
    ${OrIf} $0 = 183
        SetErrorLevel 2
        Abort
    ${EndIf}
    ${IfNot} ${IsNativeAMD64}
        MessageBox MB_OK|MB_ICONSTOP "Tabrilo requires Windows x64."
        SetErrorLevel 2
        Abort
    ${EndIf}
    ${IfNot} ${AtLeastBuild} 19041
        MessageBox MB_OK|MB_ICONSTOP "Tabrilo requires Windows build 19041 or later."
        SetErrorLevel 2
        Abort
    ${EndIf}
    ReadRegStr $Previous HKCU "${PREFS}" "Version"
    ${If} $Previous != ""
        ${VersionCompare} "$Previous" "${APP_VERSION}" $0
        ${If} $0 = 1
            MessageBox MB_OK|MB_ICONSTOP "A newer version is already installed." /SD IDOK
            SetErrorLevel 2
            Abort
        ${EndIf}
    ${EndIf}
FunctionEnd

Section "Tabrilo"
    SetRegView 64
    StrCpy $VersionPath "$INSTDIR\versions\${APP_VERSION}"
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=PayloadReceipt.exe "${RECEIPT_HELPER}"
    ; Old packages embedded one deletion list. A single legacy version can use its
    ; own uninstaller after receipt-backed versions have been removed.
    StrCpy $3 0
    StrCpy $4 0
legacyCheck:
    EnumRegValue $5 HKCU "${PREFS}\Versions" $3
    StrCmp $5 "" legacyChecked
    ReadRegStr $6 HKCU "${PREFS}\Versions" "$5"
    ${If} $6 == $5
        IntOp $4 $4 + 1
        ${If} $4 > 1
            MessageBox MB_OK|MB_ICONSTOP "Multiple older versions lack payload receipts. Resolve the earlier installation before upgrading." /SD IDOK
            SetErrorLevel 2
            Abort
        ${EndIf}
    ${EndIf}
    IntOp $3 $3 + 1
    Goto legacyCheck
legacyChecked:
    ReadRegStr $1 HKCU "${PREFS}\Versions" "${APP_VERSION}"
    ${If} $1 != ""
    ${AndIf} $1 != "${RECEIPT_HASH}"
        MessageBox MB_OK|MB_ICONSTOP "This version has a different payload or component selection. Uninstall it first or use a newer version." /SD IDOK
        SetErrorLevel 2
        Abort
    ${EndIf}
    ${If} $1 == ""
    ${AndIf} ${FileExists} "$VersionPath\*.*"
        ClearErrors
        ExecWait '"$PLUGINSDIR\PayloadReceipt.exe" check "$VersionPath" ${RECEIPT_HASH}' $0
        ${If} ${Errors}
        ${OrIf} $0 != 0
            MessageBox MB_OK|MB_ICONSTOP "The version folder already exists without a matching, complete payload." /SD IDOK
            SetErrorLevel 2
            Abort
        ${EndIf}
        ; Resume registration after interruption between publication and registration.
        StrCpy $1 "${RECEIPT_HASH}"
    ${EndIf}
    ClearErrors
    CreateDirectory "$INSTDIR\versions"
    GetTempFileName $Stage "$INSTDIR\versions"
    ${If} ${Errors}
        Goto failed
    ${EndIf}
    Delete "$Stage"
    SetOutPath "$Stage"
    File /r "${PAYLOAD}\*"
    ${If} ${Errors}
        Goto failed
    ${EndIf}
    ExecWait '"$PLUGINSDIR\PayloadReceipt.exe" check "$Stage" ${RECEIPT_HASH}' $0
    ${If} ${Errors}
    ${OrIf} $0 != 0
        Goto failed
    ${EndIf}
    SetOutPath "$INSTDIR"
    ${If} $1 == "${RECEIPT_HASH}"
        ExecWait '"$PLUGINSDIR\PayloadReceipt.exe" repair "$VersionPath" ${RECEIPT_HASH} "$Stage"' $0
        ${If} ${Errors}
        ${OrIf} $0 != 0
            Goto failed
        ${EndIf}
        ExecWait '"$PLUGINSDIR\PayloadReceipt.exe" check "$VersionPath" ${RECEIPT_HASH}' $0
        ${If} ${Errors}
        ${OrIf} $0 != 0
            Goto failed
        ${EndIf}
        !insertmacro RemovePayload "$Stage"
        StrCpy $Stage ""
        ClearErrors
        WriteUninstaller "$VersionPath\Uninstall.exe"
        ${If} ${Errors}
            Goto failed
        ${EndIf}
        Goto registered
    ${EndIf}
    WriteUninstaller "$Stage\Uninstall.exe"
    ${If} ${Errors}
        Goto failed
    ${EndIf}
    Rename "$Stage" "$VersionPath"
    ${If} ${Errors}
        Goto failed
    ${EndIf}

registered:
    SetOutPath "$INSTDIR"
    ClearErrors
    StrCpy $Stage ""
    WriteRegStr HKCU "${PREFS}\Versions" "${APP_VERSION}" "${RECEIPT_HASH}"
    WriteRegStr HKCU "${UNINSTALL}" "DisplayName" "${PRODUCT_ID}"
    WriteRegStr HKCU "${UNINSTALL}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${UNINSTALL}" "Publisher" "Umut Karabulut and contributors"
    WriteRegStr HKCU "${UNINSTALL}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL}" "UninstallString" '$\"$VersionPath\Uninstall.exe$\"'
    WriteRegDWORD HKCU "${UNINSTALL}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL}" "NoRepair" 1
    CreateShortCut "$SMPROGRAMS\${PRODUCT_ID}.lnk" "$VersionPath\Tabrilo.exe"
    ${If} ${Errors}
        Goto failed
    ${EndIf}
    ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${PRODUCT_ID}"
    ClearErrors
    ${If} $0 != ""
        WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${PRODUCT_ID}" '$\"$VersionPath\Tabrilo.exe$\"'
    ${EndIf}
    WriteRegStr HKCU "${PREFS}" "Version" "${APP_VERSION}"
    ${If} ${Errors}
        Goto failed
    ${EndIf}
    Goto complete

failed:
    SetOutPath "$INSTDIR"
    ${If} $Stage != ""
        Delete "$Stage\Uninstall.exe"
        !insertmacro RemovePayload "$Stage"
    ${EndIf}
    SetErrorLevel 1
    Abort "Tabrilo could not complete installation."
complete:
SectionEnd

Function un.onInit
    SetShellVarContext current
    SetRegView 64
    StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${PRODUCT_ID}"
FunctionEnd

Section "Uninstall"
    SetShellVarContext current
    SetRegView 64
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=PayloadReceipt.exe "${RECEIPT_HELPER}"
    StrCpy $Legacy ""
    StrCpy $0 0
versions:
    EnumRegValue $1 HKCU "${PREFS}\Versions" $0
    StrCmp $1 "" done
    IntOp $0 $0 + 1
    StrCpy $3 0
    StrCpy $4 0
validate:
    StrCpy $5 $1 1 $3
    StrCmp $5 "" validated
    IntOp $3 $3 + 1
    ${If} $5 == "."
        IntOp $4 $4 + 1
    ${ElseIf} $5 != "0"
    ${AndIf} $5 != "1"
    ${AndIf} $5 != "2"
    ${AndIf} $5 != "3"
    ${AndIf} $5 != "4"
    ${AndIf} $5 != "5"
    ${AndIf} $5 != "6"
    ${AndIf} $5 != "7"
    ${AndIf} $5 != "8"
    ${AndIf} $5 != "9"
        Goto versions
    ${EndIf}
    Goto validate
validated:
    IntCmp $4 2 known versions versions
known:
    ${If} $1 == ".."
        Goto versions
    ${EndIf}
    StrCpy $VersionPath "$INSTDIR\versions\$1"
    ReadRegStr $2 HKCU "${PREFS}\Versions" "$1"
    ${If} $2 == $1
        StrCpy $Legacy "$VersionPath"
        Goto versions
    ${EndIf}
    ClearErrors
    ClearErrors
    ExecWait '"$PLUGINSDIR\PayloadReceipt.exe" remove "$VersionPath" "$2"' $3
    ${If} ${Errors}
    ${OrIf} $3 != 0
        SetErrorLevel 1
        Abort "Could not verify or remove a version. Its registration was retained."
    ${EndIf}
    Delete "$VersionPath\Uninstall.exe"
    RMDir "$VersionPath"
    DeleteRegValue HKCU "${PREFS}\Versions" "$1"
    IntOp $0 $0 - 1
    Goto versions
done:
    ${If} $Legacy != ""
        ClearErrors
        ClearErrors
        ExecWait '"$Legacy\Uninstall.exe" /S _?=$Legacy' $3
        ${If} ${Errors}
        ${OrIf} $3 != 0
            SetErrorLevel 1
            Abort "The earlier installer could not complete removal."
        ${EndIf}
        Delete "$Legacy\Uninstall.exe"
        RMDir "$Legacy"
    ${EndIf}
    RMDir "$INSTDIR\versions"
    RMDir "$INSTDIR"
    Delete "$SMPROGRAMS\${PRODUCT_ID}.lnk"
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${PRODUCT_ID}"
    DeleteRegKey HKCU "${PREFS}"
    DeleteRegKey HKCU "${UNINSTALL}"
SectionEnd
