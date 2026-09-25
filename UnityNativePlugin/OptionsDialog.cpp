// Katanga's options window: hold Shift while starting katanga.exe.
//
// Older Unity players showed their own "Screen Selector" dialog for Shift or Alt at launch; Unity
// dropped it, and this takes its place for Katanga's own options.  The window is built from a
// description KatangaOptions.cs passes in, so the option texts live in one place.  It runs its
// own modal message loop and returns the choices; KatangaOptions.cs writes katanga_options.txt.
//
// Spec: one line per entry, fields separated by tabs:
//   section line:  "#" \t heading
//   option line:   "o" \t title \t explanation \t checked (0/1) \t value ("" = no value field)
// Result: one line per option line, "checked \t value".

#define ISOLATION_AWARE_ENABLED 1   // themed controls from a DLL, via the manifest below
#include <windows.h>
#include <commctrl.h>
#include <string>
#include <vector>
#include "Unity/IUnityInterface.h"

#pragma comment(linker, "\"/manifestdependency:type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")
#pragma comment(lib, "comctl32.lib")

namespace
{
	struct Entry
	{
		bool section = false;
		std::wstring title, help, value;
		bool checked = false;
		HWND check = nullptr, edit = nullptr;
	};

	const int IdFirstCheck = 1000;
	const int IdHelp = 900;   // explanation texts, drawn in a softer colour

	std::vector<Entry> gEntries;
	HFONT gFont = nullptr, gBold = nullptr, gHeading = nullptr;
	int gResult = -1;   // -1 running, 0 cancelled, 1 saved

	std::vector<std::wstring> Split(const std::wstring& text, wchar_t separator)
	{
		std::vector<std::wstring> parts;
		size_t start = 0;
		for (;;)
		{
			size_t end = text.find(separator, start);
			parts.push_back(text.substr(start, end == std::wstring::npos ? std::wstring::npos : end - start));
			if (end == std::wstring::npos)
				return parts;
			start = end + 1;
		}
	}

	LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
	{
		switch (msg)
		{
		case WM_COMMAND:
			if (LOWORD(wParam) == IDOK || LOWORD(wParam) == IDCANCEL)
			{
				gResult = LOWORD(wParam) == IDOK ? 1 : 0;
				return 0;
			}
			break;
		case WM_CLOSE:
			gResult = 0;
			return 0;
		case WM_CTLCOLORSTATIC:
		{
			HDC dc = (HDC)wParam;
			SetBkMode(dc, TRANSPARENT);
			if (GetDlgCtrlID((HWND)lParam) == IdHelp)
				SetTextColor(dc, RGB(90, 90, 90));
			return (LRESULT)GetSysColorBrush(COLOR_WINDOW);
		}
		}
		return DefWindowProcW(hwnd, msg, wParam, lParam);
	}

	int TextHeight(HDC dc, HFONT font, const std::wstring& text, int width)
	{
		HGDIOBJ old = SelectObject(dc, font);
		RECT r = { 0, 0, width, 0 };
		DrawTextW(dc, text.c_str(), -1, &r, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
		SelectObject(dc, old);
		return r.bottom;
	}

	int TextWidth(HDC dc, HFONT font, const std::wstring& text)
	{
		HGDIOBJ old = SelectObject(dc, font);
		SIZE size = {};
		GetTextExtentPoint32W(dc, text.c_str(), (int)text.size(), &size);
		SelectObject(dc, old);
		return size.cx;
	}

	HWND Control(HWND parent, const wchar_t* cls, const std::wstring& text, DWORD style, int x, int y, int w, int h, int id, HFONT font, DWORD exStyle = 0)
	{
		HWND control = CreateWindowExW(exStyle, cls, text.c_str(), WS_CHILD | WS_VISIBLE | style, x, y, w, h,
			parent, (HMENU)(INT_PTR)id, GetModuleHandleW(nullptr), nullptr);
		SendMessageW(control, WM_SETFONT, (WPARAM)font, FALSE);
		return control;
	}
}

extern "C" int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ShowOptionsDialog(const wchar_t* title, const wchar_t* intro,
	const wchar_t* spec, wchar_t* result, int resultLength)
{
	if (result == nullptr || resultLength <= 0)
		return 0;
	*result = 0;

	INITCOMMONCONTROLSEX icc = { sizeof(icc), ICC_STANDARD_CLASSES };
	InitCommonControlsEx(&icc);

	gEntries.clear();
	for (const std::wstring& line : Split(spec, L'\n'))
	{
		std::vector<std::wstring> f = Split(line, L'\t');
		Entry e;
		if (f.size() >= 2 && f[0] == L"#")
		{
			e.section = true;
			e.title = f[1];
		}
		else if (f.size() >= 5 && f[0] == L"o")
		{
			e.title = f[1];
			e.help = f[2];
			e.checked = f[3] == L"1";
			e.value = f[4];
		}
		else
			continue;
		gEntries.push_back(e);
	}

	// The system's message font, so the window matches the rest of Windows.
	NONCLIENTMETRICSW ncm = { sizeof(ncm) };
	SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(ncm), &ncm, 0);
	gFont = CreateFontIndirectW(&ncm.lfMessageFont);
	LOGFONTW bold = ncm.lfMessageFont;
	bold.lfWeight = FW_SEMIBOLD;
	gBold = CreateFontIndirectW(&bold);
	LOGFONTW heading = ncm.lfMessageFont;
	heading.lfWeight = FW_BOLD;
	heading.lfHeight = heading.lfHeight * 5 / 4;
	gHeading = CreateFontIndirectW(&heading);

	HDC dc = GetDC(nullptr);
	HGDIOBJ oldFont = SelectObject(dc, gFont);
	TEXTMETRICW tm = {};
	GetTextMetricsW(dc, &tm);
	SelectObject(dc, oldFont);

	const int line = tm.tmHeight;
	const int margin = line;
	const int width = tm.tmAveCharWidth * 88;              // content width
	const int checkIndent = GetSystemMetrics(SM_CXMENUCHECK) + tm.tmAveCharWidth;
	const int editWidth = tm.tmAveCharWidth * 6;

	WNDCLASSW wc = {};
	wc.lpfnWndProc = WindowProc;
	wc.hInstance = GetModuleHandleW(nullptr);
	wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
	wc.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
	wc.lpszClassName = L"KatangaOptions";
	RegisterClassW(&wc);

	DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU;
	HWND hwnd = CreateWindowExW(WS_EX_DLGMODALFRAME | WS_EX_TOPMOST, wc.lpszClassName, title, style,
		CW_USEDEFAULT, CW_USEDEFAULT, 100, 100, nullptr, nullptr, wc.hInstance, nullptr);

	int y = margin;
	int h = TextHeight(dc, gFont, intro, width);
	Control(hwnd, L"STATIC", intro, SS_LEFT | SS_NOPREFIX, margin, y, width, h, -1, gFont);
	y += h;

	int id = IdFirstCheck;
	for (Entry& e : gEntries)
	{
		if (e.section)
		{
			y += line;
			h = TextHeight(dc, gHeading, e.title, width);
			Control(hwnd, L"STATIC", e.title, SS_LEFT | SS_NOPREFIX, margin, y, width, h, -1, gHeading);
			y += h + line / 3;
			continue;
		}

		y += line / 2;
		int checkWidth = checkIndent + TextWidth(dc, gBold, e.title) + tm.tmAveCharWidth;
		e.check = Control(hwnd, L"BUTTON", e.title, BS_AUTOCHECKBOX | WS_TABSTOP, margin, y, checkWidth, line + 4, id++, gBold);
		SendMessageW(e.check, BM_SETCHECK, e.checked ? BST_CHECKED : BST_UNCHECKED, 0);
		if (!e.value.empty())
			e.edit = Control(hwnd, L"EDIT", e.value, ES_NUMBER | ES_CENTER | WS_TABSTOP, margin + checkWidth + tm.tmAveCharWidth, y,
				editWidth, line + 4, id++, gFont, WS_EX_CLIENTEDGE);
		y += line + 4;

		h = TextHeight(dc, gFont, e.help, width - checkIndent);
		Control(hwnd, L"STATIC", e.help, SS_LEFT | SS_NOPREFIX, margin + checkIndent, y + 2, width - checkIndent, h, IdHelp, gFont);
		y += h + 2;
	}

	// Buttons, right aligned.
	y += line + line / 2;
	const std::wstring save = L"Save and start", cancel = L"Start without saving";
	int saveWidth = TextWidth(dc, gFont, save) + line * 2, cancelWidth = TextWidth(dc, gFont, cancel) + line * 2;
	int buttonHeight = line * 2;
	HWND saveButton = Control(hwnd, L"BUTTON", save, BS_DEFPUSHBUTTON | WS_TABSTOP, margin + width - saveWidth - cancelWidth - line / 2, y,
		saveWidth, buttonHeight, IDOK, gFont);
	Control(hwnd, L"BUTTON", cancel, BS_PUSHBUTTON | WS_TABSTOP, margin + width - cancelWidth, y, cancelWidth, buttonHeight, IDCANCEL, gFont);
	y += buttonHeight + margin;
	ReleaseDC(nullptr, dc);

	// Size to the content and center on the monitor with the cursor.
	RECT r = { 0, 0, width + 2 * margin, y };
	AdjustWindowRectEx(&r, style, FALSE, WS_EX_DLGMODALFRAME);
	POINT cursor;
	GetCursorPos(&cursor);
	MONITORINFO mi = { sizeof(mi) };
	GetMonitorInfoW(MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY), &mi);
	int w = r.right - r.left, wh = r.bottom - r.top;
	int x = mi.rcWork.left + (mi.rcWork.right - mi.rcWork.left - w) / 2;
	int top = mi.rcWork.top + max(0L, (mi.rcWork.bottom - mi.rcWork.top - wh) / 2);
	SetWindowPos(hwnd, HWND_TOPMOST, x, top, w, wh, SWP_SHOWWINDOW);
	SetForegroundWindow(hwnd);
	SetFocus(saveButton);

	gResult = -1;
	MSG msg;
	while (gResult < 0)
	{
		BOOL got = GetMessageW(&msg, nullptr, 0, 0);
		if (got <= 0)
		{
			if (got == 0)
				PostQuitMessage((int)msg.wParam);
			gResult = 0;
			break;
		}
		if (!IsDialogMessageW(hwnd, &msg))
		{
			TranslateMessage(&msg);
			DispatchMessageW(&msg);
		}
	}

	// Collect the choices before the controls go away.
	std::wstring out;
	for (Entry& e : gEntries)
	{
		if (e.section)
			continue;
		bool checked = SendMessageW(e.check, BM_GETCHECK, 0, 0) == BST_CHECKED;
		wchar_t value[32] = L"";
		if (e.edit != nullptr)
			GetWindowTextW(e.edit, value, 32);
		out += (checked ? L"1\t" : L"0\t") + std::wstring(value) + L"\n";
	}
	DestroyWindow(hwnd);
	DeleteObject(gFont);
	DeleteObject(gBold);
	DeleteObject(gHeading);
	UnregisterClassW(wc.lpszClassName, wc.hInstance);

	wcsncpy_s(result, resultLength, out.c_str(), _TRUNCATE);
	return gResult;
}
