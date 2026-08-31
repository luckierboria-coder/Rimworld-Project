#include "PresentHook.h"

#include <algorithm>
#include <array>
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
        constexpr int kMaxHudRects = 8;

        struct GeneratedSourceSlot
        {
            ID3D11Texture2D* texture = nullptr; // borrowed
            int width = 0;
            int height = 0;
            int hudCount = 0;
            std::uint32_t frameIndex = 0;
            std::array<HudRectPx, kMaxHudRects> hud{};
        };

        std::atomic<bool> g_installed{false};
        std::atomic<IDXGISwapChain*> g_unitySwapChain{nullptr};
        ID3D11Device* g_unityDevice = nullptr; // optional Unity-owned filter.
        PresentFn g_originalPresent = nullptr;

        alignas(64) std::array<GeneratedSourceSlot, 2> g_sourceSlots{};
        std::atomic<std::uint32_t> g_sourceSequence{0};
        std::atomic<bool> g_sourceAvailable{false};
        std::atomic<std::uint64_t> g_generatedPresentCount{0};
        std::atomic<std::uint64_t> g_skippedPresentCount{0};

        ComPtr<ID3D11Texture2D> g_realScratch;
        ComPtr<ID3D11Texture2D> g_generatedComposite;
        UINT g_cachedWidth = 0;
        UINT g_cachedHeight = 0;
        DXGI_FORMAT g_cachedFormat = DXGI_FORMAT_UNKNOWN;

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

        GeneratedSourceSlot ReadLatestSource()
        {
            const std::uint32_t seq = g_sourceSequence.load(std::memory_order_acquire);
            return g_sourceSlots[seq & 1u];
        }

        void ReleasePresentResources()
        {
            g_realScratch.Reset();
            g_generatedComposite.Reset();
            g_cachedWidth = 0;
            g_cachedHeight = 0;
            g_cachedFormat = DXGI_FORMAT_UNKNOWN;
        }

        bool EnsurePresentResources(ID3D11Device* device, ID3D11Texture2D* backBuffer)
        {
            if (!device || !backBuffer)
                return false;

            D3D11_TEXTURE2D_DESC desc{};
            backBuffer->GetDesc(&desc);
            if (desc.Width == 0 || desc.Height == 0 || desc.SampleDesc.Count != 1)
                return false;

            if (g_realScratch && g_generatedComposite &&
                g_cachedWidth == desc.Width && g_cachedHeight == desc.Height && g_cachedFormat == desc.Format)
                return true;

            ReleasePresentResources();

            D3D11_TEXTURE2D_DESC scratchDesc = desc;
            scratchDesc.MipLevels = 1;
            scratchDesc.ArraySize = 1;
            scratchDesc.Usage = D3D11_USAGE_DEFAULT;
            scratchDesc.BindFlags = 0;
            scratchDesc.CPUAccessFlags = 0;
            scratchDesc.MiscFlags = 0;

            if (FAILED(device->CreateTexture2D(&scratchDesc, nullptr, &g_realScratch)))
                return false;
            if (FAILED(device->CreateTexture2D(&scratchDesc, nullptr, &g_generatedComposite)))
            {
                ReleasePresentResources();
                return false;
            }

            g_cachedWidth = desc.Width;
            g_cachedHeight = desc.Height;
            g_cachedFormat = desc.Format;
            return true;
        }

        bool CopyHudRects(
            ID3D11DeviceContext* context,
            ID3D11Texture2D* realFrame,
            ID3D11Texture2D* composite,
            const GeneratedSourceSlot& source,
            UINT width,
            UINT height)
        {
            if (!context || !realFrame || !composite)
                return false;

            for (int i = 0; i < source.hudCount; ++i)
            {
                const HudRectPx& rect = source.hud[static_cast<std::size_t>(i)];
                const LONG left = std::max<LONG>(0, static_cast<LONG>(rect.x));
                const LONG top = std::max<LONG>(0, static_cast<LONG>(rect.y));
                const LONG right = std::min<LONG>(static_cast<LONG>(width), static_cast<LONG>(rect.x + rect.width));
                const LONG bottom = std::min<LONG>(static_cast<LONG>(height), static_cast<LONG>(rect.y + rect.height));
                if (right <= left || bottom <= top)
                    continue;

                D3D11_BOX box{};
                box.left = static_cast<UINT>(left);
                box.top = static_cast<UINT>(top);
                box.front = 0;
                box.right = static_cast<UINT>(right);
                box.bottom = static_cast<UINT>(bottom);
                box.back = 1;

                context->CopySubresourceRegion(
                    composite,
                    0,
                    static_cast<UINT>(left),
                    static_cast<UINT>(top),
                    0,
                    realFrame,
                    0,
                    &box);
            }

            return true;
        }

        bool TryPresentGeneratedFrame(IDXGISwapChain* swapChain, UINT originalFlags)
        {
            if (!swapChain || !g_originalPresent || !g_sourceAvailable.load(std::memory_order_acquire))
                return false;

            // TEST presents must remain side-effect free. V0.1 also avoids exclusive
            // fullscreen until resize/flip-model behavior has been validated in-game.
            if ((originalFlags & DXGI_PRESENT_TEST) != 0)
                return false;

            DXGI_SWAP_CHAIN_DESC chainDesc{};
            if (FAILED(swapChain->GetDesc(&chainDesc)) || !chainDesc.Windowed)
                return false;

            const GeneratedSourceSlot source = ReadLatestSource();
            if (!source.texture || source.width <= 0 || source.height <= 0)
                return false;

            ComPtr<ID3D11Device> device;
            if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(device.GetAddressOf()))) || !device)
                return false;

            ComPtr<ID3D11DeviceContext> context;
            device->GetImmediateContext(&context);
            if (!context)
                return false;

            ComPtr<ID3D11Texture2D> backBuffer;
            if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backBuffer.GetAddressOf()))) || !backBuffer)
                return false;

            D3D11_TEXTURE2D_DESC backDesc{};
            D3D11_TEXTURE2D_DESC generatedDesc{};
            backBuffer->GetDesc(&backDesc);
            source.texture->GetDesc(&generatedDesc);

            // Safety gate: V0.1 injects only when the generated surface can be copied
            // byte-for-byte into the real swapchain buffer. Any mismatch falls back.
            if (backDesc.Width != generatedDesc.Width || backDesc.Height != generatedDesc.Height ||
                backDesc.Format != generatedDesc.Format || backDesc.SampleDesc.Count != 1 ||
                generatedDesc.SampleDesc.Count != 1)
                return false;

            if (!EnsurePresentResources(device.Get(), backBuffer.Get()))
                return false;

            // Preserve the complete real frame including RimWorld IMGUI/HUD.
            context->CopyResource(g_realScratch.Get(), backBuffer.Get());

            // Build generated frame entirely on GPU: generated HUD-less scene first,
            // then copy conservative HUD regions from the current real frame.
            context->CopyResource(g_generatedComposite.Get(), source.texture);
            CopyHudRects(context.Get(), g_realScratch.Get(), g_generatedComposite.Get(), source, backDesc.Width, backDesc.Height);

            // Show midpoint immediately. Do not inherit Unity's sync interval here;
            // the following real present remains the pacing authority.
            context->CopyResource(backBuffer.Get(), g_generatedComposite.Get());
            const HRESULT generatedHr = g_originalPresent(swapChain, 0, 0);
            if (FAILED(generatedHr))
                return false;

            // Flip-model swapchains advance to another backbuffer after Present, so
            // reacquire buffer 0 before restoring the real frame.
            backBuffer.Reset();
            if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backBuffer.GetAddressOf()))) || !backBuffer)
                return false;

            D3D11_TEXTURE2D_DESC restoredDesc{};
            backBuffer->GetDesc(&restoredDesc);
            if (restoredDesc.Width != backDesc.Width || restoredDesc.Height != backDesc.Height || restoredDesc.Format != backDesc.Format)
                return false;

            context->CopyResource(backBuffer.Get(), g_realScratch.Get());
            g_generatedPresentCount.fetch_add(1, std::memory_order_relaxed);
            return true;
        }

        HRESULT __stdcall HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
        {
            const bool target = IsTargetSwapChain(swapChain);
            if (target)
            {
                g_unitySwapChain.store(swapChain, std::memory_order_release);
                if (!TryPresentGeneratedFrame(swapChain, flags))
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
            }

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
        ClearGeneratedFrameSource();
        ReleasePresentResources();

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

    void SetGeneratedFrameSource(
        ID3D11Texture2D* generatedFrame,
        int width,
        int height,
        const HudRectPx* hudRects,
        int hudRectCount,
        std::uint32_t frameIndex)
    {
        if (!generatedFrame || width <= 0 || height <= 0)
        {
            ClearGeneratedFrameSource();
            return;
        }

        const std::uint32_t next = g_sourceSequence.load(std::memory_order_relaxed) + 1u;
        GeneratedSourceSlot& slot = g_sourceSlots[next & 1u];
        slot.texture = generatedFrame;
        slot.width = width;
        slot.height = height;
        slot.frameIndex = frameIndex;
        slot.hudCount = std::max(0, std::min(hudRectCount, kMaxHudRects));
        if (hudRects && slot.hudCount > 0)
        {
            for (int i = 0; i < slot.hudCount; ++i)
                slot.hud[static_cast<std::size_t>(i)] = hudRects[i];
        }

        g_sourceSequence.store(next, std::memory_order_release);
        g_sourceAvailable.store(true, std::memory_order_release);
    }

    void ClearGeneratedFrameSource()
    {
        g_sourceAvailable.store(false, std::memory_order_release);
        const std::uint32_t next = g_sourceSequence.load(std::memory_order_relaxed) + 1u;
        GeneratedSourceSlot& slot = g_sourceSlots[next & 1u];
        slot = GeneratedSourceSlot{};
        g_sourceSequence.store(next, std::memory_order_release);
    }

    std::uint64_t GeneratedPresentCount()
    {
        return g_generatedPresentCount.load(std::memory_order_acquire);
    }

    std::uint64_t SkippedPresentCount()
    {
        return g_skippedPresentCount.load(std::memory_order_acquire);
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

extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetGeneratedPresentCount()
{
    return static_cast<unsigned long long>(RimFGPresent::GeneratedPresentCount());
}

extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetSkippedPresentCount()
{
    return static_cast<unsigned long long>(RimFGPresent::SkippedPresentCount());
}

extern "C" __declspec(dllexport) void __cdecl RimFG_StopPresentHook()
{
    RimFGPresent::Shutdown();
}
