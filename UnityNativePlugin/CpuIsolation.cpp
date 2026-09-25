// CPU isolation between Katanga and the game.  --cpu-isolation [cores], --game-priority
//
// Katanga itself needs little CPU, but Virtual Desktop's OpenXR runtime runs a thread inside
// our process that spins a full core all the time (a busy-wait timer for exact frame delivery).
// With Katanga and a CPU hungry game side by side, that thread competes with the game.  Here we
// keep all of Katanga, spinning thread included, on the last physical core(s), and the game on
// all the others, so the game has its cores to itself.
//
// Cores come from the real processor layout (GetLogicalProcessorInformationEx), so the SMT
// siblings of a core always stay together.  Only processor group 0 is handled, which covers
// every desktop CPU up to 64 logical processors.

#include <windows.h>
#include <vector>
#include "Unity/IUnityInterface.h"

// Logical processor masks of the physical cores, in order.
static std::vector<KAFFINITY> PhysicalCores()
{
	std::vector<KAFFINITY> cores;
	DWORD length = 0;
	GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &length);
	std::vector<BYTE> buffer(length);
	auto* info = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)buffer.data();
	if (!GetLogicalProcessorInformationEx(RelationProcessorCore, info, &length))
		return cores;

	for (DWORD offset = 0; offset < length; )
	{
		auto* entry = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)(buffer.data() + offset);
		if (entry->Relationship == RelationProcessorCore && entry->Processor.GroupMask[0].Group == 0)
			cores.push_back(entry->Processor.GroupMask[0].Mask);
		offset += entry->Size;
	}
	return cores;
}

// Pins Katanga to the last reserveCores physical cores and the game to the rest.
// Returns the Katanga mask (0 on failure); gameMask receives the game's mask.
// gamePid 0 only moves Katanga, for use before the game is known.
extern "C" UNITY_INTERFACE_EXPORT UINT64 UNITY_INTERFACE_API ApplyCpuIsolation(int reserveCores, DWORD gamePid, UINT64* gameMask)
{
	*gameMask = 0;
	std::vector<KAFFINITY> cores = PhysicalCores();
	if (reserveCores < 1 || (int)cores.size() <= reserveCores)
		return 0;   // not enough cores to split, leave everything alone

	KAFFINITY katanga = 0, game = 0;
	for (size_t i = 0; i < cores.size(); i++)
	{
		if (i >= cores.size() - reserveCores)
			katanga |= cores[i];
		else
			game |= cores[i];
	}

	// The process mask applies to every thread in Katanga, including the ones the OpenXR
	// runtime already started, and to any started later.
	if (!SetProcessAffinityMask(GetCurrentProcess(), katanga))
		return 0;

	if (gamePid != 0)
	{
		HANDLE process = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, gamePid);
		if (process == NULL || !SetProcessAffinityMask(process, game))
		{
			if (process) CloseHandle(process);
			return 0;
		}
		CloseHandle(process);
		*gameMask = game;
	}
	return katanga;
}

// Raises the game above normal priority, so background programs yield to it.  Katanga's
// own VR frames are timed by the headset and don't need the game below them.
extern "C" UNITY_INTERFACE_EXPORT bool UNITY_INTERFACE_API RaiseGamePriority(DWORD gamePid)
{
	HANDLE process = OpenProcess(PROCESS_SET_INFORMATION, FALSE, gamePid);
	if (process == NULL)
		return false;
	bool ok = SetPriorityClass(process, ABOVE_NORMAL_PRIORITY_CLASS) != FALSE;
	CloseHandle(process);
	return ok;
}
