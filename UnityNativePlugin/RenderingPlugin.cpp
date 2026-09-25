// Example low level rendering Unity plugin

#include "PlatformBase.h"
#include "RenderAPI.h"

#include <windows.h>
#include <assert.h>
#include <math.h>
#include <vector>

#include <d3d11.h>
#include "../Shared/KatangaPacing.h"

// --------------------------------------------------------------------------
// SetTimeFromUnity, an example function we export which is called by one of the scripts.

static float g_Time;

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API SetTimeFromUnity (float t) 
{ 
	g_Time = t; 
}



// --------------------------------------------------------------------------
// SetTextureFromUnity, an example function we export which is called by one of the scripts.

static void* g_TextureHandle = NULL;
static int   g_TextureWidth = 0;
static int   g_TextureHeight = 0;

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API SetTextureFromUnity(void* textureHandle, int w, int h)
{
	// A script calls this at initialization time; just remember the texture pointer here.
	// Will update texture pixels each frame from the plugin rendering event (texture update
	// needs to happen on the rendering thread).
	g_TextureHandle = textureHandle;
	g_TextureWidth = w;
	g_TextureHeight = h;

	//do
	//{
	//	Sleep(250);
	//} while (!IsDebuggerPresent());
	//__debugbreak();

}



// --------------------------------------------------------------------------
// UnitySetInterfaces

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType);

static IUnityInterfaces* s_UnityInterfaces = NULL;
static IUnityGraphics* s_Graphics = NULL;

extern "C" void	UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
	s_UnityInterfaces = unityInterfaces;
	s_Graphics = s_UnityInterfaces->Get<IUnityGraphics>();
	s_Graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
	
	// Run OnGraphicsDeviceEvent(initialize) manually on plugin load
	OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
	s_Graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
}


// --------------------------------------------------------------------------
// GraphicsDeviceEvent


static RenderAPI* s_CurrentAPI = NULL;
static UnityGfxRenderer s_DeviceType = kUnityGfxRendererNull;


static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
{
	// Create graphics API implementation upon initialization
	if (eventType == kUnityGfxDeviceEventInitialize)
	{
		assert(s_CurrentAPI == NULL);
		s_DeviceType = s_Graphics->GetRenderer();
		s_CurrentAPI = CreateRenderAPI(s_DeviceType);
	}

	// Let the implementation process the device related events
	if (s_CurrentAPI)
	{
		s_CurrentAPI->ProcessDeviceEvent(eventType, s_UnityInterfaces);
	}

	// Cleanup graphics API implementation upon shutdown
	if (eventType == kUnityGfxDeviceEventShutdown)
	{
		delete s_CurrentAPI;
		s_CurrentAPI = NULL;
		s_DeviceType = kUnityGfxRendererNull;
	}
}




// --------------------------------------------------------------------------

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API OpenLogFile()
{
	return s_CurrentAPI->OpenLogFile();
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API CloseLogFile()
{
	return s_CurrentAPI->CloseLogFile();
}

extern "C" UNITY_INTERFACE_EXPORT ID3D11ShaderResourceView* UNITY_INTERFACE_API CreateSharedTexture(HANDLE sharedHandle)
{
	return s_CurrentAPI->CreateSharedSurface(sharedHandle);
}


extern "C" UNITY_INTERFACE_EXPORT UINT UNITY_INTERFACE_API GetGameWidth()
{
	return s_CurrentAPI->GetGameWidth();
}
extern "C" UNITY_INTERFACE_EXPORT UINT UNITY_INTERFACE_API GetGameHeight()
{
	return s_CurrentAPI->GetGameHeight();
}
extern "C" UNITY_INTERFACE_EXPORT DXGI_FORMAT UNITY_INTERFACE_API GetGameFormat()
{
	return s_CurrentAPI->GetGameFormat();
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API CreateSetupMutex()
{
	return s_CurrentAPI->CreateSetupMutex();
}
extern "C" UNITY_INTERFACE_EXPORT bool UNITY_INTERFACE_API GrabSetupMutex()
{
	return s_CurrentAPI->GrabSetupMutex();
}
extern "C" UNITY_INTERFACE_EXPORT bool UNITY_INTERFACE_API ReleaseSetupMutex()
{
	return s_CurrentAPI->ReleaseSetupMutex();
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API DestroySetupMutex()
{
	return s_CurrentAPI->DestroySetupMutex();
}

// Frame pacing.  The game and the headset each run their own clock, so even at the same
// nominal rate they drift, and the headset periodically shows a game frame twice and then
// skips one.  Katanga signals this auto-reset event once per VR frame, and GamePlugin waits
// for it in Present, which locks the game to the headset's rate and phase.  Games opened
// without this event (sync disabled, or an older Katanga) run free as before.

static HANDLE s_FrameEvent = NULL;
static HANDLE s_PacingOnlyFlag = NULL;
static KatangaPacingInfo* s_PacingView = nullptr;

extern "C" UNITY_INTERFACE_EXPORT bool UNITY_INTERFACE_API CreateFrameEvent()
{
	if (s_FrameEvent == NULL)
		s_FrameEvent = CreateEvent(NULL, FALSE, FALSE, L"KatangaFrameEvent");
	return s_FrameEvent != NULL;
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API SignalFrameEvent()
{
	if (s_FrameEvent != NULL)
		SetEvent(s_FrameEvent);
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API DestroyFrameEvent()
{
	if (s_FrameEvent != NULL)
	{
		CloseHandle(s_FrameEvent);
		s_FrameEvent = NULL;
	}
	if (s_PacingOnlyFlag != NULL)
	{
		CloseHandle(s_PacingOnlyFlag);
		s_PacingOnlyFlag = NULL;
	}
}

// For games that 3Dmigoto connects directly, GamePlugin is injected only to pace Present.
// This publishes where the real IDXGISwapChain::Present is, found outside the game where no
// 3Dmigoto wraps swap chains.  See Shared/KatangaPacing.h.  The mapping also tells
// GamePlugin to run in pacing only mode.
//
// 64 bit games: found right here in Katanga.  32 bit games: Katanga can't look into the 32 bit
// dxgi.dll, so the 32 bit GamePlugin.dll does it in a 32 bit rundll32 and fills the mapping.

static bool RunPresentProbe32(const wchar_t* gamePlugin32)
{
	wchar_t rundll32[MAX_PATH];
	GetWindowsDirectoryW(rundll32, MAX_PATH);
	wcscat_s(rundll32, MAX_PATH, L"\\SysWOW64\\rundll32.exe");

	wchar_t commandLine[2 * MAX_PATH + 64];
	swprintf_s(commandLine, L"\"%s\" \"%s\",%hs", rundll32, gamePlugin32, KATANGA_PACING_PROBE_EXPORT);

	STARTUPINFOW si = { sizeof(si) };
	PROCESS_INFORMATION pi = {};
	if (!CreateProcessW(rundll32, commandLine, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi))
		return false;
	bool finished = WaitForSingleObject(pi.hProcess, 10000) == WAIT_OBJECT_0;
	if (!finished)
		TerminateProcess(pi.hProcess, 1);
	CloseHandle(pi.hThread);
	CloseHandle(pi.hProcess);
	return finished;
}

// Returns true when the mapping holds a usable Present offset for the game's bitness.
extern "C" UNITY_INTERFACE_EXPORT bool UNITY_INTERFACE_API CreatePacingOnlyFlag(bool game32, const wchar_t* gamePlugin32)
{
	KatangaPacingInfo info = {};
	if (!game32 && !KatangaFindPresent(&info))
		return false;
	if (game32)
		info.version = KATANGA_PACING_VERSION;   // offset filled in by the 32 bit probe below

	if (s_PacingOnlyFlag == NULL)
		s_PacingOnlyFlag = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(info),
			KATANGA_PACING_MAPPING);
	if (s_PacingOnlyFlag == NULL)
		return false;
	void* view = MapViewOfFile(s_PacingOnlyFlag, FILE_MAP_WRITE, 0, 0, sizeof(info));
	if (view == nullptr)
		return false;
	memcpy(view, &info, sizeof(info));

	if (game32 && (gamePlugin32 == nullptr || !RunPresentProbe32(gamePlugin32)))
		((KatangaPacingInfo*)view)->presentRva = 0;
	bool usable = ((KatangaPacingInfo*)view)->presentRva != 0;

	// Kept mapped, to read the game's frame counter per snapshot (GamePresentCount).
	if (s_PacingView != nullptr)
		UnmapViewOfFile(s_PacingView);
	s_PacingView = (KatangaPacingInfo*)view;
	return usable;
}

// Real Presents the game has made so far, counted by GamePlugin in pacing only mode.
// -1 when there is no pacing mapping.  Katanga compares it per snapshot: +1 a new frame,
// +0 the same frame again (a stale frame in the headset), +2 or more frames skipped.
extern "C" UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API GamePresentCount()
{
	return s_PacingView != nullptr ? (int)s_PacingView->presentCount : -1;
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API OpenFileMappedIPC()
{
	return s_CurrentAPI->OpenFileMappedIPC();
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API CloseFileMappedIPC()
{
	return s_CurrentAPI->CloseFileMappedIPC();
}
extern "C" UNITY_INTERFACE_EXPORT UINT UNITY_INTERFACE_API GetSharedHandleIPC()
{
	return s_CurrentAPI->GetSharedHandleIPC();
}


static void ModifyTexturePixels()
{
	void* textureHandle = g_TextureHandle;
	int width = g_TextureWidth;
	int height = g_TextureHeight;
	if (!textureHandle)
		return;

	int textureRowPitch;
	void* textureDataPtr = s_CurrentAPI->BeginModifyTexture(textureHandle, width, height, &textureRowPitch);
	if (!textureDataPtr)
		return;

	const float t = g_Time * 4.0f;

	unsigned char* dst = (unsigned char*)textureDataPtr;
	for (int y = 0; y < height; ++y)
	{
		unsigned char* ptr = dst;
		for (int x = 0; x < width; ++x)
		{
			// Simple "plasma effect": several combined sine waves
			int vv = int(
				(127.0f + (127.0f * sinf(x / 7.0f + t))) +
				(127.0f + (127.0f * sinf(y / 5.0f - t))) +
				(127.0f + (127.0f * sinf((x + y) / 6.0f - t))) +
				(127.0f + (127.0f * sinf(sqrtf(float(x*x + y*y)) / 4.0f - t)))
				) / 4;

			// Write the texture pixel
			ptr[0] = vv;
			ptr[1] = vv;
			ptr[2] = vv;
			ptr[3] = vv;

			// To next pixel (our pixels are 4 bpp)
			ptr += 4;
		}

		// To next image row
		dst += textureRowPitch;
	}

	s_CurrentAPI->EndModifyTexture(textureHandle, width, height, textureRowPitch, textureDataPtr);
}



// --------------------------------------------------------------------------
// OnRenderEvent
// This will be called for GL.IssuePluginEvent script calls; eventID will
// be the integer passed to IssuePluginEvent. In this example, we just ignore
// that value.

static void UNITY_INTERFACE_API OnRenderEvent(int eventID)
{
	// Unknown / unsupported graphics device type? Do nothing
	if (s_CurrentAPI == NULL)
		return;

	ModifyTexturePixels();
}


// --------------------------------------------------------------------------
// GetRenderEventFunc, an example function we export which is used to get a rendering event callback function.

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API GetRenderEventFunc()
{
	return OnRenderEvent;
}


// --------------------------------------------------------------------------
// SelectGameDialog, to use windows select dialog to choose game exe.
//
// Modifies the input string to be the game path the user chooses.

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API SelectGameDialog(wchar_t *filename, int len)
{
	*filename = NULL;			// empty string default for failures/cancel.

	if (!GetAsyncKeyState(VK_LCONTROL))
		return;

	OPENFILENAME ofn = { 0 };
	ofn.lStructSize = sizeof(ofn);
	ofn.hwndOwner = NULL;  // If you have a window to center over, put its HANDLE here
	ofn.lpstrFilter = L".exe\0*.exe\0\0";
	ofn.lpstrFile = filename;	// Input filename, becomes primary return value.
	ofn.nMaxFile = len;
	ofn.lpstrTitle = L"Select a Game Exe to launch in Virtual 3D.";
	ofn.Flags = OFN_DONTADDTORECENT | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;

	GetOpenFileName(&ofn);
}


// --------------------------------------------------------------------------
// TriggerEvent, an example function we export which is used to trigger the Event
// object in the game process.  That will allow the drawing to continue at Present.

//extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API TriggerEvent(HANDLE eventHandle)
//{
//	BOOL res = SetEvent(eventHandle);
//	if (!res)
//		__debugbreak();
//}


