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

// 32 bit games: Katanga is 64 bit and can't look into the 32 bit dxgi.dll.  It runs
//   %windir%\SysWOW64\rundll32.exe "<Plugins>\GamePlugin.dll",ProbePresent
// which finds Present the same way in that clean 32 bit process and fills in the mapping.
#define KATANGA_PACING_PROBE_EXPORT "ProbePresent"

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

#ifdef __d3d11_h__
// Finds IDXGISwapChain::Present in this process with a throwaway swap chain, and fills in
// info.  Only valid in a process where nothing wraps swap chains (Katanga, the probe),
// never inside a 3Dmigoto game.  Returns false and leaves presentRva 0 on failure.
inline bool KatangaFindPresent(KatangaPacingInfo* info)
{
	*info = {};
	info->version = KATANGA_PACING_VERSION;

	HMODULE d3d11 = LoadLibraryW(L"d3d11.dll");
	PFN_D3D11_CREATE_DEVICE_AND_SWAP_CHAIN create = d3d11 == NULL ? nullptr :
		(PFN_D3D11_CREATE_DEVICE_AND_SWAP_CHAIN)GetProcAddress(d3d11, "D3D11CreateDeviceAndSwapChain");
	if (create == nullptr)
		return false;

	HWND window = CreateWindowExW(0, L"STATIC", L"KatangaPacingProbe", WS_OVERLAPPED, 0, 0, 16, 16,
		NULL, NULL, NULL, NULL);

	DXGI_SWAP_CHAIN_DESC desc = {};
	desc.BufferCount = 1;
	desc.BufferDesc.Width = 16;
	desc.BufferDesc.Height = 16;
	desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
	desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
	desc.OutputWindow = window;
	desc.SampleDesc.Count = 1;
	desc.Windowed = TRUE;
	desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

	IDXGISwapChain* swapChain = nullptr;
	ID3D11Device* device = nullptr;
	ID3D11DeviceContext* context = nullptr;
	if (SUCCEEDED(create(nullptr, D3D_DRIVER_TYPE_HARDWARE, NULL, 0, nullptr, 0, D3D11_SDK_VERSION,
		&desc, &swapChain, &device, nullptr, &context)))
	{
		// IDXGISwapChain::Present is vtable slot 8.  It must live in dxgi.dll itself.
		BYTE* present = (BYTE*)(*(void***)swapChain)[8];
		HMODULE dxgi = GetModuleHandleW(L"dxgi.dll");
		HMODULE owner = NULL;
		if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
			(LPCWSTR)present, &owner) && owner == dxgi &&
			KatangaModuleIdentity(dxgi, &info->dxgiTimeDateStamp, &info->dxgiSizeOfImage))
			info->presentRva = present - (BYTE*)dxgi;
	}

	if (context) context->Release();
	if (swapChain) swapChain->Release();
	if (device) device->Release();
	if (window) DestroyWindow(window);
	return info->presentRva != 0;
}
#endif
