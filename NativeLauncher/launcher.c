// zapret UI — нативный загрузчик.
// Единственный файл, который получает пользователь (~1 МБ, без всякого рантайма):
// 1) Ставит .NET Desktop Runtime 7, если его нет.
// 2) Распаковывает встроенное приложение в %LOCALAPPDATA%\Programs\zapret-ui\<хеш>
//    и запускает его с указанием папки zapret.
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <urlmon.h>
#include <stdio.h>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "urlmon.lib")
#pragma comment(lib, "advapi32.lib")

#define DESKTOP_RT_DIR L"C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App"
#define RUNTIME_URL L"https://aka.ms/dotnet/7.0/windowsdesktop-runtime-win-x64.exe"
#define RUNTIME_PAGE L"https://dotnet.microsoft.com/download/dotnet/7.0"

static BOOL IsAdmin(void)
{
    HANDLE tok;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) return FALSE;
    TOKEN_ELEVATION ei;
    DWORD len = sizeof(ei);
    BOOL ok = GetTokenInformation(tok, TokenElevation, &ei, sizeof(ei), &len);
    CloseHandle(tok);
    return ok && ei.TokenIsElevated;
}

// Любой Desktop Runtime версии 7.0+ подходит (rollForward Minor по умолчанию)
static BOOL HasDesktopRuntime(void)
{
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(DESKTOP_RT_DIR L"\\*", &fd);
    if (h == INVALID_HANDLE_VALUE) return FALSE;
    BOOL found = FALSE;
    do
    {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
        {
            const wchar_t *n = fd.cFileName;
            if (n[0] >= L'7' && n[0] <= L'9' && n[1] == L'.') { found = TRUE; break; }
            if (n[0] >= L'1' && n[1] >= L'0' && n[1] <= L'9') { found = TRUE; break; } // 10.x, 11.x...
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return found;
}

// FNV-1a 64 по собственному файлу — имя папки для этой сборки payload
static void SelfHash(wchar_t *out, int outLen)
{
    unsigned long long h = 1469598103934665603ULL;
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(NULL, self, MAX_PATH);
    HANDLE f = CreateFileW(self, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f != INVALID_HANDLE_VALUE)
    {
        unsigned char buf[65536];
        DWORD n;
        while (ReadFile(f, buf, sizeof(buf), &n, NULL) && n > 0)
            for (DWORD i = 0; i < n; i++) h = (h ^ buf[i]) * 1099511628211ULL;
        CloseHandle(f);
    }
    _snwprintf_s(out, outLen, _TRUNCATE, L"%016llx", h);
}

// Ресурсы 101..104 — payload: dll, runtimeconfig, deps, exe
static const struct { int id; const wchar_t *name; } kPayload[] = {
    { 101, L"zapret-ui.dll" },
    { 102, L"zapret-ui.runtimeconfig.json" },
    { 103, L"zapret-ui.deps.json" },
    { 104, L"zapret-ui.exe" },
};

static BOOL ExtractResource(int id, const wchar_t *path)
{
    HRSRC r = FindResourceW(NULL, MAKEINTRESOURCEW(id), RT_RCDATA);
    if (!r) return FALSE;
    HGLOBAL g = LoadResource(NULL, r);
    void *data = LockResource(g);
    DWORD size = SizeofResource(NULL, r);
    HANDLE f = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return FALSE;
    DWORD written = 0;
    BOOL ok = WriteFile(f, data, size, &written, NULL) && written == size;
    CloseHandle(f);
    return ok;
}

int APIENTRY wWinMain(HINSTANCE inst, HINSTANCE prev, PWSTR cmdLine, int show)
{
    (void)inst; (void)prev; (void)show;
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(NULL, self, MAX_PATH);

    if (!IsAdmin())
    {
        // Перезапуск от имени администратора (программе нужны права для службы zapret)
        HINSTANCE r = ShellExecuteW(NULL, L"runas", self, cmdLine, NULL, SW_HIDE);
        if ((INT_PTR)r <= 32)
            MessageBoxW(NULL, L"Нужны права администратора: запустите файл ещё раз и подтвердите запрос.",
                L"zapret UI", MB_ICONWARNING);
        return 0;
    }

    if (!HasDesktopRuntime())
    {
        wchar_t tmp[MAX_PATH];
        GetTempPathW(MAX_PATH, tmp);
        wcscat_s(tmp, MAX_PATH, L"windowsdesktop-runtime-7-x64.exe");
        MessageBoxW(NULL, L"Для zapret UI нужен .NET Desktop Runtime 7.\n\n"
            L"Сейчас он скачается с сайта Microsoft (~55 МБ) и установится автоматически. "
            L"После установки программа откроется сама.",
            L"zapret UI", MB_ICONINFORMATION | MB_OK);
        HRESULT hr = URLDownloadToFileW(NULL, RUNTIME_URL, tmp, 0, NULL);
        if (SUCCEEDED(hr))
        {
            wchar_t cmd[MAX_PATH + 64];
            _snwprintf_s(cmd, MAX_PATH + 64, _TRUNCATE, L"\"%s\" /install /quiet /norestart", tmp);
            STARTUPINFOW si = { sizeof(si) };
            PROCESS_INFORMATION pi;
            if (CreateProcessW(NULL, cmd, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi))
            {
                WaitForSingleObject(pi.hProcess, 600000);
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            DeleteFileW(tmp);
        }
        if (!HasDesktopRuntime())
        {
            MessageBoxW(NULL, L"Не удалось установить .NET Desktop Runtime автоматически.\n\n"
                L"Открою страницу загрузки — скачайте «.NET Desktop Runtime 7», столбец x64 "
                L"(раздел Installers для Windows), и запустите этот файл заново.",
                L"zapret UI", MB_ICONWARNING);
            ShellExecuteW(NULL, L"open", RUNTIME_PAGE, NULL, NULL, SW_SHOWNORMAL);
            return 1;
        }
    }

    // Папка с хешем сборки: новая версия распакуется рядом со старой, не мешая работающей копии
    wchar_t hash[32];
    SelfHash(hash, 32);
    wchar_t base[MAX_PATH], appDir[MAX_PATH];
    GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH);
    _snwprintf_s(base, MAX_PATH, _TRUNCATE, L"%s\\Programs\\zapret-ui", base);
    _snwprintf_s(appDir, MAX_PATH, _TRUNCATE, L"%s\\%s", base, hash);
    SHCreateDirectoryExW(NULL, appDir, NULL);
    for (int i = 0; i < 4; i++)
    {
        wchar_t path[MAX_PATH];
        _snwprintf_s(path, MAX_PATH, _TRUNCATE, L"%s\\%s", appDir, kPayload[i].name);
        ExtractResource(kPayload[i].id, path);
    }
    // Убираем папки других сборок
    WIN32_FIND_DATAW fd;
    wchar_t findMask[MAX_PATH];
    _snwprintf_s(findMask, MAX_PATH, _TRUNCATE, L"%s\\*", base);
    HANDLE h = FindFirstFileW(findMask, &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                && wcscmp(fd.cFileName, L".") != 0 && wcscmp(fd.cFileName, L"..") != 0
                && _wcsicmp(fd.cFileName, hash) != 0)
            {
                wchar_t dir[MAX_PATH];
                _snwprintf_s(dir, MAX_PATH, _TRUNCATE, L"%s\\%s", base, fd.cFileName);
                // SHFileOperation без возможности отката
                wchar_t spec[MAX_PATH + 2];
                _snwprintf_s(spec, MAX_PATH + 2, _TRUNCATE, L"%s\\", dir);
                SHFILEOPSTRUCTW op = { 0 };
                op.wFunc = FO_DELETE;
                op.pFrom = spec;
                op.fFlags = FOF_NO_UI | FOF_NOCONFIRMATION;
                SHFileOperationW(&op);
            }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }

    wchar_t root[MAX_PATH];
    GetModuleFileNameW(NULL, root, MAX_PATH);
    wchar_t *slash = wcsrchr(root, L'\\');
    if (slash) slash[1] = 0;

    wchar_t cmd[16384];
    _snwprintf_s(cmd, 16384, _TRUNCATE,
        L"\"%s\\zapret-ui.exe\" -ZapretRoot \"%s\" -Launcher \"%s\" %s",
        appDir, root, self, cmdLine ? cmdLine : L"");
    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi;
    if (!CreateProcessW(NULL, cmd, NULL, NULL, FALSE, 0, NULL, root, &si, &pi))
    {
        MessageBoxW(NULL, L"Не удалось запустить zapret UI.", L"zapret UI", MB_ICONERROR);
        return 1;
    }
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return 0;
}
