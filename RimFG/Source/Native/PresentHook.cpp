#include "PresentHook.h"

#include <atomic>
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <MinHook.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace RimFGPresent
{
    namespace
    {
        using PresentFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);

        std::atomic<bool> g_installed{false};
        std::atomic<IDXGISwapChain*> g_unitySwapChain{nullptr};
        ID3D11Device* g_unityDevice = nullptr; // optional Unity-owned filter.
        PresentFn g_originalPresent = nullptr;

        LRESULT CALLBACK DummyWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
        {
            return DefWindowProc(hwnd, msg, wp, lp);
        }

        bool IsCurrentProcessWindow(HWND hwnd)
        {
            if (!hwnd)
                return false;

            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            if (pid != GetCurrentProcessId())
                return false;

            RECT rc{};
            if (!GetClientRect(hwnd, &rc))
                return false;

            // Ignore helper/thumbnail/tiny swapchains. RimWorld's render target is
            // expected to be a normal game window with a meaningful client area.
            const LONG width = rc.right - rc.left;
            const LONG height = rc.bottom - rc.top;
            return width >= 320 && height >= 240;
        }

        bool IsTargetSwapChain(IDXGISwapChain* swapChain)
        {
            if (!swapChain)
                return false;

            if (g_unityDevice)
            {
                ComPtr<ID3D11Device> device;
                if (SUCCEEDED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(device.GetAddressOf()))) &&
                    device.Get() == g_unityDevice)
                    return true;
            }

            DXGI_SWAP_CHAIN_DESC desc{};
            if (FAILED(swapChain->GetDesc(&desc)))
                return false;

            return IsCurrentProcessWindow(desc.OutputWindow);
        }

        HRESULT __stdcall HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
        {
            if (IsTargetSwapChain(swapChain))
                g_unitySwapChain.store(swapChain, std::memory_order_release);

            return g_originalPresent ? g_originalPresent(swapChain, syncInterval, flags) : E_FAIL;
        }

        bool ResolvePresentAddress(void** outAddress)
        {
            if (!outAddress)
                return false;
            *outAddress = nullptr;

            const wchar_t* className = L"RimFG_DummyDX11Window";
            WNDCLASSEXW wc{};
            wc.cbSize = sizeof(wc);
            wc.lpfnWndProc = DummyWndProc;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = className;

            const ATOM atom = RegisterClassExW(&wc);
            if (!atom && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
                return false;

            HWND hwnd = CreateWindowExW(
                0, className, L"", WS_OVERLAPPEDWINDOW,
                0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
            if (!hwnd)
                return false;

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
            ComPtr<IDXGISwapChain> swapChain;

            const HRESULT hr = D3D11CreateDeviceAndSwapChain(
                nullptr,
                D3D_DRIVER_TYPE_HARDWARE,
                nullptr,
                0,
                requested,
                1,
                D3D11_SDK_VERSION,
                &desc,
                &swapChain,
                &device,
                &created,
                &context);

            if (SUCCEEDED(hr) && swapChain)
            {
                void** vtable = *reinterpret_cast<void***>(swapChain.Get());
                *outAddress = vtable[8]; // IDXGISwapChain::Present
            }

            DestroyWindow(hwnd);
            UnregisterClassW(className, wc.hInstance);
            return *outAddress != nullptr;
        }
    }

    bool Initialize(ID3D11Device* unityDevice)
    {
        if (unityDevice)
            g_unityDevice = unityDevice;

        if (g_installed.load(std::memory_order_acquire))
            return true;

        void* presentAddress = nullptr;
        if (!ResolvePresentAddress(&presentAddress))
            return false;

        const MH_STATUS initStatus = MH_Initialize();
        if (initStatus != MH_OK && initStatus != MH_ERROR_ALREADY_INITIALIZED)
            return false;

        if (MH_CreateHook(presentAddress, &HookPresent, reinterpret_cast<void**>(&g_originalPresent)) != MH_OK)
        {
            MH_Uninitialize();
            return false;
        }

        if (MH_EnableHook(presentAddress) != MH_OK)
        {
            MH_RemoveHook(presentAddress);
            MH_Uninitialize();
            g_originalPresent = nullptr;
            return false;
        }

        g_installed.store(true, std::memory_order_release);
        return true;
    }

    void Shutdown()
    {
        if (g_installed.exchange(false, std::memory_order_acq_rel))
        {
            MH_DisableHook(MH_ALL_HOOKS);
            MH_Uninitialize();
        }

        g_originalPresent = nullptr;
        g_unitySwapChain.store(nullptr, std::memory_order_release);
        g_unityDevice = nullptr;
    }

    bool IsInstalled()
    {
        return g_installed.load(std::memory_order_acquire);
    }

    bool HasUnitySwapChain()
    {
        return g_unitySwapChain.load(std::memory_order_acquire) != nullptr;
    }

    IDXGISwapChain* GetUnitySwapChain()
    {
        return g_unitySwapChain.load(std::memory_order_acquire);
    }
}

extern "C" __declspec(dllexport) int __cdecl RimFG_StartPresentHook()
{
    return RimFGPresent::Initialize(nullptr) ? 1 : 0;
}

extern "C" __declspec(dllexport) int __cdecl RimFG_HasUnitySwapChain()
{
    return RimFGPresent::HasUnitySwapChain() ? 1 : 0;
}

extern "C" __declspec(dllexport) void __cdecl RimFG_StopPresentHook()
{
    RimFGPresent::Shutdown();
}
