#pragma once
#include "stdafx.h"
#include <ShlObj.h>
#include <ShlGuid.h>
#include <shellapi.h>


typedef void* (__cdecl*MFCALLBACK)(int msg, void* lParam, void* wParam);

EXTERN_C M_API HICON MFM_GetFileIcon(LPWSTR extention, LPWSTR s, int count);
EXTERN_C M_API HICON MFM_GetFolderIcon();
EXTERN_C M_API HICON MFM_GetMyComputerIcon();
EXTERN_C M_API void MFM_GetRoots();
EXTERN_C M_API void MFM_SetCallBack(MFCALLBACK cp);
EXTERN_C M_API BOOL MFM_GetFolders(LPWSTR path);
EXTERN_C M_API BOOL MFM_OpenFile(LPWSTR path, HWND hWnd);
EXTERN_C M_API BOOL MFM_ReUpdateFile(LPWSTR fullPath, LPWSTR dirPath);
EXTERN_C M_API BOOL MFM_UpdateFile(LPWSTR fullPath, LPWSTR dirPath);
EXTERN_C M_API BOOL MFM_GetFiles(LPWSTR path);
EXTERN_C M_API BOOL MFM_GetFileTime(FILETIME * ft, LPWSTR s, int count);
EXTERN_C M_API BOOL MFM_GetFileAttr(DWORD att, LPWSTR s, int count, BOOL*hiddenout);
EXTERN_C M_API void MFM_Refesh();
EXTERN_C M_API void MFM_Recall(int id, LPWSTR path);
EXTERN_C M_API int MFM_CopyOrCutFileToClipboard(LPWSTR szFileName, BOOL isCopy);
EXTERN_C M_API void MFM_SetStatus(LPWSTR st);
EXTERN_C M_API void MFM_SetStatus2(int st);
EXTERN_C M_API BOOL MFM_IsValidateFolderFileName(wchar_t * pName);
EXTERN_C M_API BOOL MFM_CreateDir(wchar_t * path);
EXTERN_C M_API BOOL MFM_DeleteDirOrFile(wchar_t * path);
EXTERN_C M_API BOOL MFM_DeleteDir(const wchar_t * szFileDir);
EXTERN_C M_API BOOL MFM_IsPathDir(const wchar_t * path);
BOOL MFM_RenameFile();
BOOL MFM_MoveFileToUser();
BOOL MFM_CopyFileToUser();
BOOL MFM_DelFileToRecBinUser();
BOOL MFM_DelFileBinUser();
void MFF_ShowFolderProp();
void MFF_CopyPath();
void MFF_ShowInExplorer();
BOOL MFF_DelToRecBin();
BOOL MFF_Del();
void MFF_Copy();
void MFF_Patse();
void MFF_Cut();
void MFF_Remane();
void MFF_ShowFolder();
EXTERN_C M_API LPWSTR MFM_GetSeledItemPath(int index);
EXTERN_C M_API void MFM_GetSeledItemFree(void* v);
EXTERN_C M_API void MFM_SetShowHiddenFiles(BOOL b);
void MFM_ReSetShowHiddenFiles();
EXTERN_C M_API int MAppWorkShowMenuFM(LPWSTR strFilePath, BOOL mutilSelect, int selectCount);
EXTERN_C M_API int MAppWorkShowMenuFMF(LPWSTR strfolderPath);
EXTERN_C M_API LPWSTR MFM_GetMyComputerName();

