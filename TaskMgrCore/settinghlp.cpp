#include "stdafx.h"
#include "settinghlp.h"
#include "mapphlp.h"
#include "PathHelper.h"
#include "StringHlp.h"

WCHAR iniPath[MAX_PATH];

M_CAPI(LPWSTR) M_CFG_GetCfgFilePath() {
	return iniPath;
}
M_CAPI(BOOL) M_CFG_GetConfigBOOL(LPWSTR configkey, LPWSTR configSection, BOOL defaultValue)
{
	BOOL rs = defaultValue;

	WCHAR temp[32];
	if (GetPrivateProfileString(configSection, configkey, defaultValue ? L"TRUE" : L"FALSE", temp, 32, iniPath) > 0)
		defaultValue = StrEqual(temp, L"1") || StrEqual(temp, L"TRUE") || StrEqual(temp, L"True");
	return defaultValue;
}
M_CAPI(BOOL) M_CFG_SetConfigBOOL(LPWSTR configkey, LPWSTR configSection, BOOL value)
{
	return WritePrivateProfileStringW(configSection, configkey, value ? L"TRUE" : L"FALSE", iniPath);
}