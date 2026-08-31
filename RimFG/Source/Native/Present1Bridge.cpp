#include <atomic>
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <MinHook.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace
{
    using Present1Fn = HRESULT(__stdcall*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

    std::atomic<bool> g_present1Installed{false};
    Present1Fn g_originalPresent1 = nullptr;

    LRESULT CALLBACK DummyWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
    {
        return DefWindowProc(hwnd, msg, wp, lp);
    }

    bool IsCurrentProcessWindow(HWND hwnd)
    {
        if (!hwnd) return false;
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid != GetCurrentProcessId()) return false;
        RECT rc{};
        if (!GetClientRect(hwnd, &rc)) return false;
        return (rc.right - rc.left) >= 320 && (rc.bottom - rc.top) >= 240;
    }

    bool IsTargetSwapChain(IDXGISwapChain1* swapChain)
    {
        if (!swapChain) return false;
        HWND hwnd = nullptr;
        if (SUCCEEDED(swapChain->GetHwnd(&hwnd)) && IsCurrentProcessWindow(hwnd))
            return true;

        DXGI_SWAP_CHAIN_DESC desc{};
        return SUCCEEDED(swapChain->GetDesc(&desc)) && IsCurrentProcessWindow(desc.OutputWindow);
    }

    HRESULT __stdcall HookPresent1(IDXGISwapChain1* swapChain, UINT syncInterval, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
    {
        if (IsTargetSwapChain(swapChain))
        {
            // Compatibility bridge for Unity builds that submit through Present1.
            // Route the actual display call through inherited IDXGISwapChain::Present,
            // which is already intercepted by RimFG's primary Present hook. This lets
            // the existing path capture the real RimWorld swapchain, bootstrap D3D11,
            // composite generated frames, and keep a single presentation authority.
            // Present and Present1 are equivalent when no dirty-rect/scroll metadata is
            // required; Unity normally submits a full-frame Present here.
            (void)params;
            return swapChain->Present(syncInterval, flags);
        }

        return g_originalPresent1 ? g_originalPresent1(swapChain, syncInterval, flags, params) : E_FAIL;
    }

    bool ResolvePresent1Address(void** outAddress)
    {
        if (!outAddress) return false;
        *outAddress = nullptr;

        const wchar_t* className = L"RimFG_DummyDX11Window_Present1";
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = DummyWndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = className;
        const ATOM atom = RegisterClassExW(&wc);
        if (!atom && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        HWND hwnd = CreateWindowExW(0, className, L"", WS_OVERLAPPEDWINDOW,
            0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
        if (!hwnd) return false;

        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferCount = 1;
        desc.BufferDesc.Width = 64;
        desc.BufferDesc.Height = 64;
        desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.OutputWindow = hwnd;
        desc.SampleDesc.Count = 1;
        desc.Windowed = TRUE;
        desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_0 };
        D3D_FEATURE_LEVEL created{};
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<IDXGISwapChain> chain;
        const HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE,
            nullptr, 0, requested, 1, D3D11_SDK_VERSION, &desc, &chain, &device, &created, &context);

        if (SUCCEEDED(hr) && chain)
        {
            ComPtr<IDXGISwapChain1> chain1;
            if (SUCCEEDED(chain.As(&chain1)) && chain1)
            {
                void** vtable = *reinterpret_cast<void***>(chain1.Get());
                // IDXGISwapChain1 adds Present1 at vtable index 22.
                *outAddress = vtable[22];
            }
        }

        DestroyWindow(hwnd);
        UnregisterClassW(className, wc.hInstance);
        return *outAddress != nullptr;
    }
}

extern "C" __declspec(dllexport) int __cdecl RimFG_StartPresent1Bridge()
{
    if (g_present1Installed.load(std::memory_order_acquire)) return 1;

    void* present1Address = nullptr;
    if (!ResolvePresent1Address(&present1Address)) return 0;

    const MH_STATUS init = MH_Initialize();
    if (init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED) return 0;

    const MH_STATUS create = MH_CreateHook(present1Address, &HookPresent1, reinterpret_cast<void**>(&g_originalPresent1));
    if (create != MH_OK && create != MH_ERROR_ALREADY_CREATED) return 0;

    const MH_STATUS enable = MH_EnableHook(present1Address);
    if (enable != MH_OK && enable != MH_ERROR_ENABLED) return 0;

    g_present1Installed.store(true, std::memory_order_release);
    return 1;
}
