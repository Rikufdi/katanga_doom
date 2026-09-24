#pragma once

// Shared between Katanga (UnityNativePlugin) and the game side (GamePlugin, pacing only mode).
//
// To pace a game that 3Dmigoto connects to Katanga directly, GamePlugin hooks the real
// dxgi.dll IDXGISwapChain::Present.  Inside the game that address can't be found from a
// throwaway swap chain, because 3Dmigoto wraps every swap chain created there.  System DLLs
// load at the same address in every process for the whole boot, so Katanga finds Present in
// its own clean process and passes its offset in dxgi.dll here.  The game side only uses it
// when its dxgi.dll is the exact same build (PE timestamp and image size).
//
// The mapping existing at all is what tells GamePlugin to run in pacing only mode.

#include <windows.h>

#define KATANGA_PACING_MAPPING L"Local\\KatangaPacing"

struct KatangaPacingInfo
{
	UINT32 version;              // KATANGA_PACING_VERSION
	UINT32 dxgiTimeDateStamp;    // IMAGE_FILE_HEADER.TimeDateStamp of dxgi.dll
	UINT32 dxgiSizeOfImage;      // IMAGE_OPTIONAL_HEADER.SizeOfImage of dxgi.dll
	UINT32 reserved;
	UINT64 presentRva;           // IDXGISwapChain::Present offset in dxgi.dll, 0 if unknown
};

#define KATANGA_PACING_VERSION 1

// PE identity of a loaded module, to check both processes run the same dxgi.dll.
inline bool KatangaModuleIdentity(HMODULE module, UINT32* timeDateStamp, UINT32* sizeOfImage)
{
	if (module == NULL)
		return false;
	IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)module;
	IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)((BYTE*)module + dos->e_lfanew);
	*timeDateStamp = nt->FileHeader.TimeDateStamp;
	*sizeOfImage = nt->OptionalHeader.SizeOfImage;
	return true;
}
