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

// --isolate-spinner: give the OpenXR runtime's spinning thread a logical CPU of its own.
//
// Virtual Desktop's runtime keeps one thread inside Katanga spinning a full core for exact
// frame timing.  Here that thread gets the last logical CPU Katanga may use, and every other
// Katanga thread, plus the game, keeps off it.  Costs the game one logical CPU, not a core.
//
// The spinner is found by measuring, not by name, so any runtime is covered: a thread using
// more than half a core over a sample.  It only spins while Katanga runs its frame loop, so
// the sampling runs on its own thread and never blocks Katanga.  Up to 5 tries, a second
// apart.  If nothing spins, nothing is changed.

#include <tlhelp32.h>
#include <map>

static ULONGLONG ThreadCpu100ns(HANDLE thread)
{
	FILETIME created, exited, kernel, user;
	if (!GetThreadTimes(thread, &created, &exited, &kernel, &user))
		return 0;
	return (((ULONGLONG)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime) +
	       (((ULONGLONG)user.dwHighDateTime << 32) | user.dwLowDateTime);
}

// 0 running, 1 isolated, 2 no spinner found, 3 not possible
static volatile LONG s_spinnerState = 0;
static volatile LONG s_spinnerCount = 0;
static UINT64 s_spinnerCpu = 0;
static DWORD s_spinnerGamePid = 0;

static DWORD WINAPI IsolateSpinnerThread(LPVOID)
{
	DWORD_PTR processMask, systemMask;
	GetProcessAffinityMask(GetCurrentProcess(), &processMask, &systemMask);
	int last = -1;
	for (int i = 0; i < 64; i++)
		if (processMask & (1ull << i))
			last = i;
	if (last <= 0)
	{
		InterlockedExchange(&s_spinnerState, 3);
		return 0;
	}
	KAFFINITY spinnerCpu = 1ull << last;
	KAFFINITY otherCpus = processMask & ~spinnerCpu;

	DWORD pid = GetCurrentProcessId();
	DWORD self = GetCurrentThreadId();
	const DWORD sampleMs = 500;

	for (int attempt = 0; attempt < 5; attempt++)
	{
		std::map<DWORD, HANDLE> threads;
		std::map<DWORD, ULONGLONG> before;
		HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
		if (snap == INVALID_HANDLE_VALUE)
			break;
		THREADENTRY32 te = { sizeof(te) };
		for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te))
		{
			if (te.th32OwnerProcessID != pid || te.th32ThreadID == self)
				continue;
			HANDLE h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION | THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, FALSE, te.th32ThreadID);
			if (h == NULL)
				continue;
			threads[te.th32ThreadID] = h;
			before[te.th32ThreadID] = ThreadCpu100ns(h);
		}
		CloseHandle(snap);

		Sleep(sampleMs);

		std::vector<HANDLE> spinners, others;
		for (auto& t : threads)
		{
			ULONGLONG used = ThreadCpu100ns(t.second) - before[t.first];
			(used > (ULONGLONG)sampleMs * 10000 / 2 ? spinners : others).push_back(t.second);
		}

		if (!spinners.empty())
		{
			for (HANDLE h : spinners)
				SetThreadAffinityMask(h, spinnerCpu);
			for (HANDLE h : others)
				SetThreadAffinityMask(h, otherCpus);
			if (s_spinnerGamePid != 0)
			{
				HANDLE game = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, s_spinnerGamePid);
				if (game != NULL)
				{
					DWORD_PTR gameMask, sys;
					if (GetProcessAffinityMask(game, &gameMask, &sys) && (gameMask & ~spinnerCpu) != 0)
						SetProcessAffinityMask(game, gameMask & ~spinnerCpu);
					CloseHandle(game);
				}
			}
			s_spinnerCpu = spinnerCpu;
			InterlockedExchange(&s_spinnerCount, (LONG)spinners.size());
		}

		for (auto& t : threads)
			CloseHandle(t.second);

		if (s_spinnerCount > 0)
		{
			InterlockedExchange(&s_spinnerState, 1);
			return 0;
		}
		Sleep(1000);
	}

	InterlockedExchange(&s_spinnerState, 2);
	return 0;
}

// Starts the isolation in the background.  gamePid 0 if there is no game.
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API StartSpinnerIsolation(DWORD gamePid)
{
	s_spinnerGamePid = gamePid;
	InterlockedExchange(&s_spinnerState, 0);
	InterlockedExchange(&s_spinnerCount, 0);
	HANDLE thread = CreateThread(NULL, 0, IsolateSpinnerThread, NULL, 0, NULL);
	if (thread != NULL)
		CloseHandle(thread);
	else
		InterlockedExchange(&s_spinnerState, 3);
}

// 0 still running, 1 isolated (spinners on spinnerCpu), 2 no spinner found, 3 not possible.
extern "C" UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API SpinnerIsolationResult(int* spinners, UINT64* spinnerCpu)
{
	*spinners = s_spinnerCount;
	*spinnerCpu = s_spinnerCpu;
	return s_spinnerState;
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
