// openvr_api.dll stand-in for 3DFixManager.
//
// 3DFixManager checks for a VR headset through OpenVR before launching Katanga, as an
// overlay application, and OpenVR starts SteamVR for that if it isn't running.  Katanga
// itself uses OpenXR (Virtual Desktop's runtime here), and SteamVR fighting Virtual Desktop
// for the headset made SteamVR hang and restart over and over.
//
// This DLL replaces 3dfixmanager\Assemblies\openvr_api.dll and answers 3DFixManager's calls
// the way a running SteamVR with a headset would, without starting anything:
//   - VR_InitInternal2 succeeds, IVRSystem_021 is "available"
//   - Prop_ModelNumber_String      -> Model   (default "Meta Quest 3")
//   - Prop_DisplayFrequency_Float  -> Hz      (default 90)
// Override both in openvr_stub.ini next to this DLL:
//   [Headset]
//   Model=Meta Quest 3
//   Hz=90
//
// 3DFixManager can use the frequency for its "headset Hz" frame limiter choices.
//
// Every call is logged to openvr_stub.log next to the DLL.
//
// Optional extra, not part of Katanga itself.  See README.md in this folder for install,
// uninstall and limitations.  Build with build.bat from an x64 VS tools prompt, or:
//   cl /LD /O2 /EHsc openvr_stub.cpp /Fe:openvr_api.dll /link /DEF:openvr_stub.def

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>

typedef int EVRInitError;
typedef int ETrackedPropertyError;

static const int VRInitError_None = 0;
static const int VRInitError_Init_InterfaceNotFound = 105;
static const int TrackedProp_Success = 0;
static const int TrackedProp_BufferTooSmall = 3;
static const int TrackedProp_UnknownProperty = 4;

static const int Prop_TrackingSystemName_String = 1000;
static const int Prop_ModelNumber_String = 1001;
static const int Prop_SerialNumber_String = 1002;
static const int Prop_DisplayFrequency_Float = 2002;

static char g_folder[MAX_PATH];
static char g_model[128] = "Meta Quest 3";
static float g_hz = 90.0f;
static const char* g_runtimePath = "C:\\Program Files (x86)\\Steam\\steamapps\\common\\SteamVR";

static void Log(const char* fmt, ...)
{
	char path[MAX_PATH];
	snprintf(path, MAX_PATH, "%s\\openvr_stub.log", g_folder);
	FILE* f = nullptr;
	if (fopen_s(&f, path, "a") != 0 || !f)
		return;
	SYSTEMTIME t;
	GetLocalTime(&t);
	fprintf(f, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
	va_list args;
	va_start(args, fmt);
	vfprintf(f, fmt, args);
	va_end(args);
	fputc('\n', f);
	fclose(f);
}

static void LoadConfig(HMODULE self)
{
	GetModuleFileNameA(self, g_folder, MAX_PATH);
	char* slash = strrchr(g_folder, '\\');
	if (slash)
		*slash = 0;

	char ini[MAX_PATH], hz[32];
	snprintf(ini, MAX_PATH, "%s\\openvr_stub.ini", g_folder);
	GetPrivateProfileStringA("Headset", "Model", g_model, g_model, sizeof(g_model), ini);
	GetPrivateProfileStringA("Headset", "Hz", "90", hz, sizeof(hz), ini);
	g_hz = (float)atof(hz);
	if (g_hz < 30.0f)
		g_hz = 90.0f;
}

// ---- IVRSystem_021 function table.  47 slots in the order of openvr.h; 3DFixManager only
// calls the property getters.  Everything else returns zero and does nothing.

static uint64_t __stdcall Unused() { return 0; }

static float __stdcall GetFloatTrackedDeviceProperty(uint32_t device, int prop, ETrackedPropertyError* error)
{
	Log("GetFloatTrackedDeviceProperty(%u, %d)", device, prop);
	if (device == 0 && prop == Prop_DisplayFrequency_Float)
	{
		if (error) *error = TrackedProp_Success;
		return g_hz;
	}
	if (error) *error = TrackedProp_UnknownProperty;
	return 0.0f;
}

static uint32_t __stdcall GetStringTrackedDeviceProperty(uint32_t device, int prop, char* buffer, uint32_t size, ETrackedPropertyError* error)
{
	Log("GetStringTrackedDeviceProperty(%u, %d, size %u)", device, prop, size);
	const char* value = nullptr;
	if (device == 0 && prop == Prop_ModelNumber_String)
		value = g_model;
	else if (device == 0 && prop == Prop_TrackingSystemName_String)
		value = "katanga_openvr_stub";
	else if (device == 0 && prop == Prop_SerialNumber_String)
		value = "STUB";

	if (!value)
	{
		if (error) *error = TrackedProp_UnknownProperty;
		return 0;
	}
	uint32_t needed = (uint32_t)strlen(value) + 1;
	if (buffer && size >= needed)
	{
		memcpy(buffer, value, needed);
		if (error) *error = TrackedProp_Success;
	}
	else if (error)
	{
		*error = TrackedProp_BufferTooSmall;
	}
	return needed;
}

static const int kSlots = 47;
static const int kSlotFloatProperty = 23;
static const int kSlotStringProperty = 28;
static void* g_system[kSlots];

// ---- Exports used by the C# binding (Valve.VR.OpenVRInterop).

extern "C" uint32_t VR_InitInternal2(EVRInitError* error, int applicationType, const char* startupInfo)
{
	Log("VR_InitInternal2(type %d) -> success, no SteamVR started", applicationType);
	if (error) *error = VRInitError_None;
	return 1;
}

extern "C" uint32_t VR_InitInternal(EVRInitError* error, int applicationType)
{
	return VR_InitInternal2(error, applicationType, "");
}

extern "C" void VR_ShutdownInternal()
{
	Log("VR_ShutdownInternal");
}

extern "C" BOOL VR_IsHmdPresent() { return TRUE; }
extern "C" BOOL VR_IsRuntimeInstalled() { return TRUE; }
extern "C" const char* VR_RuntimePath() { return g_runtimePath; }

extern "C" BOOL VR_GetRuntimePath(char* buffer, uint32_t size, uint32_t* required)
{
	uint32_t needed = (uint32_t)strlen(g_runtimePath) + 1;
	if (required) *required = needed;
	if (!buffer || size < needed)
		return FALSE;
	memcpy(buffer, g_runtimePath, needed);
	return TRUE;
}

extern "C" const char* VR_GetStringForHmdError(EVRInitError error) { return "openvr stub"; }
extern "C" const char* VR_GetVRInitErrorAsSymbol(EVRInitError error) { return "VRInitError_None"; }
extern "C" const char* VR_GetVRInitErrorAsEnglishDescription(EVRInitError error) { return "No error (openvr stub)"; }
extern "C" BOOL VR_IsInterfaceVersionValid(const char* version) { return TRUE; }
extern "C" uint32_t VR_GetInitToken() { return 1; }

extern "C" void* VR_GetGenericInterface(const char* version, EVRInitError* error)
{
	Log("VR_GetGenericInterface(%s)", version ? version : "null");
	if (version && strcmp(version, "FnTable:IVRSystem_021") == 0)
	{
		if (error) *error = VRInitError_None;
		return g_system;
	}
	if (error) *error = VRInitError_Init_InterfaceNotFound;
	return nullptr;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_ATTACH)
	{
		for (int i = 0; i < kSlots; i++)
			g_system[i] = (void*)&Unused;
		g_system[kSlotFloatProperty] = (void*)&GetFloatTrackedDeviceProperty;
		g_system[kSlotStringProperty] = (void*)&GetStringTrackedDeviceProperty;
		LoadConfig(module);
		Log("loaded, reporting %s at %.0f Hz", g_model, g_hz);
	}
	return TRUE;
}
