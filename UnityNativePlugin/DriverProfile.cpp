// NVIDIA driver profiles: for Katanga itself, and vsync off for the game during a VR session
// (GameVsyncOff / RestoreGameVsync, further down).
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
#include <stdio.h>
#include <io.h>
#include <string>
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

// --------------------------------------------------------------------------------------------
// Vsync off for the game, for this VR session only.
//
// In exclusive fullscreen, 3DFixManager's forced global vsync locks the game to the monitor's
// refresh (60 fps on a 60 Hz TV against a 72/90 Hz headset: steady judder).  Before the launch
// Katanga forces vsync off in the game's program profile, and restores the profile as soon as the
// game runs (the driver has read its settings by then), or at the latest when the game or Katanga
// exits.  The original state goes into a journal file first; if Katanga dies before restoring,
// the next start finds the journal and restores it.  Only the two vsync entries are touched.
//
// Journal lines: profile=<name>  created=<0|1>  vsync=<hex|absent>  tear=<hex|absent>

enum GameVsyncResult
{
	GameVsync_AlreadyOff = 0,    // the game's profile already forces vsync off, nothing changed
	GameVsync_Changed = 1,       // forced off now, journal written
	GameVsync_NoNvidia = 2,
	GameVsync_Failed = 3,
	GameVsync_NothingToRestore = 4,
	GameVsync_Restored = 5,
};

static const wchar_t* kTempProfilePrefix = L"Katanga VR session: ";

// The value stored in this profile itself, or false if it only inherits one.
static bool StoredSetting(NvDRSSessionHandle session, NvDRSProfileHandle profile, NvU32 id, NvU32* value)
{
	NVDRS_SETTING setting = {};
	setting.version = NVDRS_SETTING_VER;
	if (NvAPI_DRS_GetSetting(session, profile, id, &setting) != NVAPI_OK || setting.settingLocation != NVDRS_CURRENT_PROFILE_LOCATION)
		return false;
	*value = setting.u32CurrentValue;
	return true;
}

static NvAPI_Status OpenSession(NvDRSSessionHandle* session)
{
	if (NvAPI_Initialize() != NVAPI_OK)
		return NVAPI_NVIDIA_DEVICE_NOT_FOUND;
	NvAPI_Status st = NvAPI_DRS_CreateSession(session);
	if (st == NVAPI_OK)
		st = NvAPI_DRS_LoadSettings(*session);
	return st;
}

extern "C" UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API GameVsyncOff(const wchar_t* exeName, const wchar_t* journalPath, int* status)
{
	*status = NVAPI_OK;
	NvDRSSessionHandle session = 0;
	NvAPI_Status st = OpenSession(&session);
	if (st == NVAPI_NVIDIA_DEVICE_NOT_FOUND)
		return GameVsync_NoNvidia;

	int result = GameVsync_Failed;
	NvDRSProfileHandle profile = 0;
	bool created = false;
	if (st == NVAPI_OK)
	{
		NVDRS_APPLICATION app = {};
		app.version = NVDRS_APPLICATION_VER;
		st = NvAPI_DRS_FindApplicationByName(session, (NvU16*)exeName, &profile, &app);
		if (st == NVAPI_EXECUTABLE_NOT_FOUND)
		{
			// No profile holds the game: a temporary one, deleted again on restore.
			NVDRS_PROFILE info = {};
			info.version = NVDRS_PROFILE_VER;
			swprintf_s((wchar_t*)info.profileName, NVAPI_UNICODE_STRING_MAX, L"%s%s", kTempProfilePrefix, exeName);
			st = NvAPI_DRS_CreateProfile(session, &info, &profile);
			if (st == NVAPI_OK)
			{
				NVDRS_APPLICATION add = {};
				add.version = NVDRS_APPLICATION_VER;
				wcscpy_s((wchar_t*)add.appName, NVAPI_UNICODE_STRING_MAX, exeName);
				st = NvAPI_DRS_CreateApplication(session, profile, &add);
				created = true;
			}
		}
	}

	if (st == NVAPI_OK)
	{
		NvU32 vsync = 0, tear = 0;
		bool hasVsync = StoredSetting(session, profile, VSYNCMODE_ID, &vsync);
		bool hasTear = StoredSetting(session, profile, VSYNCTEARCONTROL_ID, &tear);
		if (!created && hasVsync && vsync == VSYNCMODE_FORCEOFF)
		{
			result = GameVsync_AlreadyOff;
		}
		else
		{
			NVDRS_PROFILE info = {};
			info.version = NVDRS_PROFILE_VER;
			st = NvAPI_DRS_GetProfileInfo(session, profile, &info);

			// The journal first, so a crash from here on can always be undone.
			FILE* f = nullptr;
			if (st == NVAPI_OK && _wfopen_s(&f, journalPath, L"w, ccs=UTF-8") == 0 && f != nullptr)
			{
				fwprintf(f, L"profile=%s\ncreated=%d\n", (wchar_t*)info.profileName, created ? 1 : 0);
				if (hasVsync) fwprintf(f, L"vsync=%08X\n", vsync); else fwprintf(f, L"vsync=absent\n");
				if (hasTear) fwprintf(f, L"tear=%08X\n", tear); else fwprintf(f, L"tear=absent\n");
				fflush(f);
				FlushFileBuffers((HANDLE)_get_osfhandle(_fileno(f)));
				fclose(f);

				st = SetDword(session, profile, VSYNCMODE_ID, VSYNCMODE_FORCEOFF);
				if (st == NVAPI_OK)
					st = SetDword(session, profile, VSYNCTEARCONTROL_ID, VSYNCTEARCONTROL_DISABLE);
				if (st == NVAPI_OK)
					st = NvAPI_DRS_SaveSettings(session);
				if (st == NVAPI_OK)
					result = GameVsync_Changed;
			}
			else if (st == NVAPI_OK)
				st = NVAPI_ERROR;   // no journal, no change
		}
	}

	if (result == GameVsync_Failed)
		*status = st;
	if (session)
		NvAPI_DRS_DestroySession(session);
	return result;
}

static void RestoreSetting(NvDRSSessionHandle session, NvDRSProfileHandle profile, NvU32 id, const std::wstring& value, NvAPI_Status* st)
{
	if (*st != NVAPI_OK)
		return;
	if (value == L"absent")
	{
		NvAPI_Status del = NvAPI_DRS_DeleteProfileSetting(session, profile, id);
		if (del != NVAPI_OK && del != NVAPI_SETTING_NOT_FOUND)
			*st = del;
	}
	else
		*st = SetDword(session, profile, id, (NvU32)wcstoul(value.c_str(), nullptr, 16));
}

extern "C" UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API RestoreGameVsync(const wchar_t* journalPath, wchar_t* profileName, int profileNameLength, int* status)
{
	*status = NVAPI_OK;
	if (profileName && profileNameLength > 0)
		*profileName = 0;

	FILE* f = nullptr;
	if (_wfopen_s(&f, journalPath, L"r, ccs=UTF-8") != 0 || f == nullptr)
		return GameVsync_NothingToRestore;
	std::wstring name, created, vsync, tear;
	wchar_t line[512];
	while (fgetws(line, 512, f))
	{
		std::wstring l(line);
		while (!l.empty() && (l.back() == L'\n' || l.back() == L'\r'))
			l.pop_back();
		size_t eq = l.find(L'=');
		if (eq == std::wstring::npos)
			continue;
		std::wstring key = l.substr(0, eq), value = l.substr(eq + 1);
		if (key == L"profile") name = value;
		else if (key == L"created") created = value;
		else if (key == L"vsync") vsync = value;
		else if (key == L"tear") tear = value;
	}
	fclose(f);
	if (profileName && profileNameLength > 0)
		wcsncpy_s(profileName, profileNameLength, name.c_str(), _TRUNCATE);

	NvDRSSessionHandle session = 0;
	NvAPI_Status st = OpenSession(&session);
	int result = GameVsync_Failed;
	if (st == NVAPI_OK && !name.empty() && !vsync.empty() && !tear.empty())
	{
		NvDRSProfileHandle profile = 0;
		st = NvAPI_DRS_FindProfileByName(session, (NvU16*)name.c_str(), &profile);
		if (st == NVAPI_PROFILE_NOT_FOUND)
			st = NVAPI_OK;          // gone already (deleted by hand): nothing left to restore
		else if (st == NVAPI_OK)
		{
			if (created == L"1")
				st = NvAPI_DRS_DeleteProfile(session, profile);
			else
			{
				RestoreSetting(session, profile, VSYNCMODE_ID, vsync, &st);
				RestoreSetting(session, profile, VSYNCTEARCONTROL_ID, tear, &st);
			}
			if (st == NVAPI_OK)
				st = NvAPI_DRS_SaveSettings(session);
		}
		if (st == NVAPI_OK)
			result = GameVsync_Restored;
	}
	else if (st == NVAPI_OK)
		st = NVAPI_INVALID_ARGUMENT;   // unreadable journal

	if (result == GameVsync_Restored)
		DeleteFileW(journalPath);    // only once the profile is back
	else
		*status = st;
	if (session)
		NvAPI_DRS_DestroySession(session);
	return result;
}
