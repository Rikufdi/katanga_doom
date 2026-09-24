// NVIDIA driver profile for Katanga itself.
//
// 3DFixManager sets NVIDIA's *global* vsync to On whenever it launches a game (for 3D TV
// users).  Katanga presents a desktop mirror window every frame, and with vsync forced on
// that Present waits for the desktop display: on a 60 Hz TV the whole VR loop then runs
// at 60 fps on a 90 Hz headset.  A program profile for katanga.exe with vsync forced off
// overrides the global setting.  This makes sure that profile exists.
//
// The driver reads program profiles when a process starts, so a profile created now takes
// effect from the next launch.  Writing driver settings needs admin rights, which Katanga
// has when 3DFixManager starts it.

#include <windows.h>
#include "../DeviarePlugin/nvapi/nvapi.h"

#pragma comment(lib, "../DeviarePlugin/nvapi/nvapi64.lib")

#include "Unity/IUnityInterface.h"

// From NVIDIA's NvApiDriverSettings.h, and the same values 3DFixManager uses.
static const NvU32 VSYNCMODE_ID = 0x00A879CF;
static const NvU32 VSYNCMODE_FORCEOFF = 0x08416747;
static const NvU32 VSYNCTEARCONTROL_ID = 0x005A375C;
static const NvU32 VSYNCTEARCONTROL_DISABLE = 0x96861077;

static const wchar_t* kProfileName = L"Katanga VR";

// Results for LaunchAndPlay to log.
enum DriverProfileResult
{
	Profile_AlreadySet = 0,      // katanga.exe already has vsync forced off
	Profile_Updated = 1,         // written now, takes effect next launch
	Profile_NoNvidia = 2,        // no NVIDIA driver, nothing to do
	Profile_Failed = 3,          // see status
};

static bool SettingIs(NvDRSSessionHandle session, NvDRSProfileHandle profile, NvU32 id, NvU32 value)
{
	NVDRS_SETTING setting = {};
	setting.version = NVDRS_SETTING_VER;
	if (NvAPI_DRS_GetSetting(session, profile, id, &setting) != NVAPI_OK)
		return false;
	// Only a value stored in this profile counts, not one inherited from the global profile.
	return setting.settingLocation == NVDRS_CURRENT_PROFILE_LOCATION && setting.u32CurrentValue == value;
}

static NvAPI_Status SetDword(NvDRSSessionHandle session, NvDRSProfileHandle profile, NvU32 id, NvU32 value)
{
	NVDRS_SETTING setting = {};
	setting.version = NVDRS_SETTING_VER;
	setting.settingId = id;
	setting.settingType = NVDRS_DWORD_TYPE;
	setting.u32CurrentValue = value;
	return NvAPI_DRS_SetSetting(session, profile, &setting);
}

// status receives the failing NvAPI_Status for Profile_Failed.
extern "C" UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API EnsureKatangaDriverProfile(int* status)
{
	*status = NVAPI_OK;
	if (NvAPI_Initialize() != NVAPI_OK)
		return Profile_NoNvidia;

	// The exe name as it runs, normally katanga.exe.
	wchar_t exePath[MAX_PATH];
	GetModuleFileNameW(NULL, exePath, MAX_PATH);
	const wchar_t* exeName = wcsrchr(exePath, L'\\') ? wcsrchr(exePath, L'\\') + 1 : exePath;

	NvDRSSessionHandle session = 0;
	int result = Profile_Failed;
	NvAPI_Status st = NvAPI_DRS_CreateSession(&session);
	if (st == NVAPI_OK)
		st = NvAPI_DRS_LoadSettings(session);

	NvDRSProfileHandle profile = 0;
	if (st == NVAPI_OK)
	{
		// Reuse whatever profile already holds katanga.exe, including one made by hand in the
		// NVIDIA Control Panel.
		NVDRS_APPLICATION app = {};
		app.version = NVDRS_APPLICATION_VER;
		st = NvAPI_DRS_FindApplicationByName(session, (NvU16*)exeName, &profile, &app);
		if (st == NVAPI_EXECUTABLE_NOT_FOUND)
		{
			NVDRS_PROFILE info = {};
			info.version = NVDRS_PROFILE_VER;
			wcscpy_s((wchar_t*)info.profileName, NVAPI_UNICODE_STRING_MAX, kProfileName);
			st = NvAPI_DRS_CreateProfile(session, &info, &profile);
			if (st == NVAPI_PROFILE_NAME_IN_USE)
				st = NvAPI_DRS_FindProfileByName(session, (NvU16*)kProfileName, &profile);
			if (st == NVAPI_OK)
			{
				NVDRS_APPLICATION add = {};
				add.version = NVDRS_APPLICATION_VER;
				wcscpy_s((wchar_t*)add.appName, NVAPI_UNICODE_STRING_MAX, exeName);
				wcscpy_s((wchar_t*)add.userFriendlyName, NVAPI_UNICODE_STRING_MAX, L"Katanga VR");
				st = NvAPI_DRS_CreateApplication(session, profile, &add);
			}
		}
	}

	if (st == NVAPI_OK)
	{
		if (SettingIs(session, profile, VSYNCMODE_ID, VSYNCMODE_FORCEOFF))
		{
			result = Profile_AlreadySet;
		}
		else
		{
			st = SetDword(session, profile, VSYNCMODE_ID, VSYNCMODE_FORCEOFF);
			if (st == NVAPI_OK)
				st = SetDword(session, profile, VSYNCTEARCONTROL_ID, VSYNCTEARCONTROL_DISABLE);
			if (st == NVAPI_OK)
				st = NvAPI_DRS_SaveSettings(session);
			if (st == NVAPI_OK)
				result = Profile_Updated;
		}
	}

	if (result == Profile_Failed)
		*status = st;
	if (session)
		NvAPI_DRS_DestroySession(session);
	return result;
}
