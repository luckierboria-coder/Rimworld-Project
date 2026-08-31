#include "PresentHook.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <thread>
#include <windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi.h>
#include <MinHook.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace RimFGPresent
{
    namespace
    {
        using PresentFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);
        using Clock = std::chrono::steady_clock;
        constexpr int kMaxHudRects = 8;

        struct GeneratedSourceSlot
        {
            ID3D11Texture2D* texture = nullptr;
            int width = 0;
            int height = 0;
            int hudCount = 0;
            std::uint32_t frameIndex = 0;
            std::array<HudRectPx, kMaxHudRects> hud{};
        };

        struct AsyncFrameSlot
        {
            ComPtr<ID3D11Texture2D> composite;
            ComPtr<IDXGISwapChain> swapChain;
            UINT width = 0;
            UINT height = 0;
            DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
            std::uint64_t sequence = 0;
        };

        std::atomic<bool> g_installed{false};
        std::atomic<IDXGISwapChain*> g_unitySwapChain{nullptr};
        std::atomic<int> g_presentMode{static_cast<int>(PresentMode::ImmediateValidation)};
        std::atomic<int> g_targetOutputFps{60};
        std::atomic<BackbufferGenerationCallback> g_generationCallback{nullptr};
        ID3D11Device* g_unityDevice = nullptr;
        PresentFn g_originalPresent = nullptr;

        alignas(64) std::array<GeneratedSourceSlot, 2> g_sourceSlots{};
        std::atomic<std::uint32_t> g_sourceSequence{0};
        std::atomic<bool> g_sourceAvailable{false};
        std::atomic<std::uint64_t> g_generatedPresentCount{0};
        std::atomic<std::uint64_t> g_skippedPresentCount{0};

        std::array<AsyncFrameSlot, 2> g_asyncSlots{};
        ComPtr<ID3D11Device> g_asyncDevice;
        ComPtr<ID3D11DeviceContext> g_asyncContext;
        ComPtr<ID3D11Multithread> g_multithread;
        UINT g_asyncWidth = 0;
        UINT g_asyncHeight = 0;
        DXGI_FORMAT g_asyncFormat = DXGI_FORMAT_UNKNOWN;

        std::thread g_presenterThread;
        std::mutex g_presenterMutex;
        std::condition_variable g_presenterCv;
        bool g_presenterStop = false;
        std::uint64_t g_preparedSequence = 0;
        std::uint64_t g_scheduledSequence = 0;
        Clock::time_point g_scheduledDeadline{};
        Clock::time_point g_lastRealPresent{};
        double g_realIntervalSeconds = 1.0 / 30.0;
        double g_generationQuota = 0.0;

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

        bool IsTargetSwapChain(IDXGISwapChain* swapChain)
        {
            if (!swapChain) return false;
            if (g_unityDevice)
            {
                ComPtr<ID3D11Device> device;
                if (SUCCEEDED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(device.GetAddressOf()))) && device.Get() == g_unityDevice)
                    return true;
            }
            DXGI_SWAP_CHAIN_DESC desc{};
            return SUCCEEDED(swapChain->GetDesc(&desc)) && IsCurrentProcessWindow(desc.OutputWindow);
        }

        GeneratedSourceSlot ReadLatestSource()
        {
            const std::uint32_t seq = g_sourceSequence.load(std::memory_order_acquire);
            return g_sourceSlots[seq & 1u];
        }

        void CopyHudRects(ID3D11DeviceContext* context, ID3D11Texture2D* realFrame, ID3D11Texture2D* composite, const GeneratedSourceSlot& source, UINT width, UINT height)
        {
            for (int i = 0; i < source.hudCount; ++i)
            {
                const auto& rect = source.hud[static_cast<std::size_t>(i)];
                const LONG left = std::max<LONG>(0, static_cast<LONG>(rect.x));
                const LONG top = std::max<LONG>(0, static_cast<LONG>(rect.y));
                const LONG right = std::min<LONG>(static_cast<LONG>(width), static_cast<LONG>(rect.x + rect.width));
                const LONG bottom = std::min<LONG>(static_cast<LONG>(height), static_cast<LONG>(rect.y + rect.height));
                if (right <= left || bottom <= top) continue;
                D3D11_BOX box{static_cast<UINT>(left), static_cast<UINT>(top), 0, static_cast<UINT>(right), static_cast<UINT>(bottom), 1};
                context->CopySubresourceRegion(composite, 0, static_cast<UINT>(left), static_cast<UINT>(top), 0, realFrame, 0, &box);
            }
        }

        void ReleaseAsyncResources()
        {
            for (auto& slot : g_asyncSlots)
            {
                slot.composite.Reset();
                slot.swapChain.Reset();
                slot.width = 0;
                slot.height = 0;
                slot.format = DXGI_FORMAT_UNKNOWN;
                slot.sequence = 0;
            }
            g_multithread.Reset();
            g_asyncContext.Reset();
            g_asyncDevice.Reset();
            g_asyncWidth = 0;
            g_asyncHeight = 0;
            g_asyncFormat = DXGI_FORMAT_UNKNOWN;
        }

        bool EnsureAsyncResources(ID3D11Device* device, ID3D11Texture2D* backBuffer)
        {
            if (!device || !backBuffer) return false;
            D3D11_TEXTURE2D_DESC desc{};
            backBuffer->GetDesc(&desc);
            if (!desc.Width || !desc.Height || desc.SampleDesc.Count != 1) return false;

            if (g_asyncDevice.Get() == device && g_asyncContext &&
                g_asyncWidth == desc.Width && g_asyncHeight == desc.Height && g_asyncFormat == desc.Format &&
                g_asyncSlots[0].composite && g_asyncSlots[1].composite)
                return true;

            ReleaseAsyncResources();
            g_asyncDevice = device;
            device->GetImmediateContext(&g_asyncContext);
            if (!g_asyncContext) return false;

            g_asyncContext.As(&g_multithread);
            if (g_multithread)
                g_multithread->SetMultithreadProtected(TRUE);

            D3D11_TEXTURE2D_DESC copy = desc;
            copy.MipLevels = 1;
            copy.ArraySize = 1;
            copy.Usage = D3D11_USAGE_DEFAULT;
            copy.BindFlags = 0;
            copy.CPUAccessFlags = 0;
            copy.MiscFlags = 0;

            if (FAILED(device->CreateTexture2D(&copy, nullptr, &g_asyncSlots[0].composite)) ||
                FAILED(device->CreateTexture2D(&copy, nullptr, &g_asyncSlots[1].composite)))
            {
                ReleaseAsyncResources();
                return false;
            }

            g_asyncWidth = desc.Width;
            g_asyncHeight = desc.Height;
            g_asyncFormat = desc.Format;
            return true;
        }

        std::uint64_t PrepareAsyncGeneratedFrame(IDXGISwapChain* swapChain)
        {
            if (!swapChain || !g_sourceAvailable.load(std::memory_order_acquire)) return 0;
            const GeneratedSourceSlot source = ReadLatestSource();
            if (!source.texture || source.width <= 0 || source.height <= 0) return 0;

            ComPtr<ID3D11Device> device;
            if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(device.GetAddressOf()))) || !device) return 0;
            ComPtr<ID3D11Texture2D> backBuffer;
            if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backBuffer.GetAddressOf()))) || !backBuffer) return 0;

            D3D11_TEXTURE2D_DESC backDesc{}, sourceDesc{};
            backBuffer->GetDesc(&backDesc);
            source.texture->GetDesc(&sourceDesc);
            if (backDesc.Width != sourceDesc.Width || backDesc.Height != sourceDesc.Height || backDesc.Format != sourceDesc.Format ||
                backDesc.SampleDesc.Count != 1 || sourceDesc.SampleDesc.Count != 1)
                return 0;
            if (!EnsureAsyncResources(device.Get(), backBuffer.Get())) return 0;

            const std::uint64_t next = g_preparedSequence + 1u;
            AsyncFrameSlot& slot = g_asyncSlots[next & 1u];
            g_asyncContext->CopyResource(slot.composite.Get(), source.texture);
            CopyHudRects(g_asyncContext.Get(), backBuffer.Get(), slot.composite.Get(), source, backDesc.Width, backDesc.Height);
            slot.swapChain = swapChain;
            slot.width = backDesc.Width;
            slot.height = backDesc.Height;
            slot.format = backDesc.Format;
            slot.sequence = next;
            g_preparedSequence = next;
            return next;
        }

        bool UpdateRealTimingAndDecideGeneration(Clock::time_point now)
        {
            if (g_lastRealPresent.time_since_epoch().count() != 0)
            {
                const double seconds = std::chrono::duration<double>(now - g_lastRealPresent).count();
                if (seconds >= 1.0 / 240.0 && seconds <= 1.0 / 8.0)
                    g_realIntervalSeconds += (seconds - g_realIntervalSeconds) * 0.12;
            }
            g_lastRealPresent = now;

            const double baseFps = 1.0 / std::max(1.0 / 240.0, g_realIntervalSeconds);
            const double targetFps = static_cast<double>(std::max(1, g_targetOutputFps.load(std::memory_order_acquire)));
            if (targetFps <= baseFps + 0.5)
            {
                g_generationQuota = 0.0;
                return false;
            }

            // V0.1 supports one generated frame per real frame. Therefore the useful
            // generated/real ratio is [0,1]. A later 3x mode can extend this without
            // changing the target-FPS UI semantics.
            const double generatedPerReal = std::max(0.0, std::min(1.0, targetFps / baseFps - 1.0));
            g_generationQuota = std::min(2.0, g_generationQuota + generatedPerReal);
            if (g_generationQuota < 1.0)
                return false;

            g_generationQuota -= 1.0;
            return true;
        }

        void ScheduleGeneratedFrame(std::uint64_t sequence, Clock::time_point now)
        {
            if (!sequence) return;
            const double targetPeriod = 1.0 / static_cast<double>(std::max(1, g_targetOutputFps.load(std::memory_order_acquire)));
            const double delay = std::max(0.002, std::min(0.030, std::min(g_realIntervalSeconds * 0.5, targetPeriod)));
            {
                std::lock_guard<std::mutex> lock(g_presenterMutex);
                if (g_scheduledSequence != 0 && g_scheduledSequence != sequence)
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
                g_scheduledSequence = sequence;
                g_scheduledDeadline = now + std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(delay));
            }
            g_presenterCv.notify_one();
        }

        void PresenterMain()
        {
            std::unique_lock<std::mutex> lock(g_presenterMutex);
            while (!g_presenterStop)
            {
                g_presenterCv.wait(lock, [] { return g_presenterStop || g_scheduledSequence != 0; });
                if (g_presenterStop) break;

                const std::uint64_t sequence = g_scheduledSequence;
                const Clock::time_point deadline = g_scheduledDeadline;
                if (g_presenterCv.wait_until(lock, deadline, [sequence] { return g_presenterStop || g_scheduledSequence != sequence; }))
                    continue;
                if (g_presenterStop) break;

                AsyncFrameSlot slot = g_asyncSlots[sequence & 1u];
                if (slot.sequence != sequence || !slot.swapChain || !slot.composite)
                {
                    g_scheduledSequence = 0;
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
                    continue;
                }
                g_scheduledSequence = 0;
                lock.unlock();

                bool presented = false;
                if (static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire)) != PresentMode::Disabled && g_originalPresent)
                {
                    ComPtr<ID3D11Texture2D> backBuffer;
                    if (SUCCEEDED(slot.swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backBuffer.GetAddressOf()))) && backBuffer && g_asyncContext)
                    {
                        D3D11_TEXTURE2D_DESC desc{};
                        backBuffer->GetDesc(&desc);
                        if (desc.Width == slot.width && desc.Height == slot.height && desc.Format == slot.format && desc.SampleDesc.Count == 1)
                        {
                            g_asyncContext->CopyResource(backBuffer.Get(), slot.composite.Get());
                            HRESULT hr = g_originalPresent(slot.swapChain.Get(), 0, DXGI_PRESENT_DO_NOT_WAIT);
                            // Never fall back to a potentially blocking Present here.
                            presented = SUCCEEDED(hr);
                        }
                    }
                }

                if (presented)
                    g_generatedPresentCount.fetch_add(1, std::memory_order_relaxed);
                else
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);

                lock.lock();
            }
        }

        HRESULT __stdcall HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
        {
            const bool targetSwapChain = IsTargetSwapChain(swapChain);
            const bool isRealPresent = targetSwapChain && (flags & DXGI_PRESENT_TEST) == 0;
            const Clock::time_point now = Clock::now();
            bool wantsGenerated = false;
            std::uint64_t prepared = 0;
            PresentMode mode = PresentMode::Disabled;

            if (targetSwapChain)
            {
                g_unitySwapChain.store(swapChain, std::memory_order_release);
                mode = static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire));
                if (isRealPresent)
                    wantsGenerated = mode != PresentMode::Disabled && UpdateRealTimingAndDecideGeneration(now);

                BackbufferGenerationCallback callback = g_generationCallback.load(std::memory_order_acquire);
                if (callback && isRealPresent)
                {
                    ComPtr<ID3D11Texture2D> backBuffer;
                    if (SUCCEEDED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backBuffer.GetAddressOf()))) && backBuffer)
                        callback(backBuffer.Get());
                }

                if (wantsGenerated)
                    prepared = PrepareAsyncGeneratedFrame(swapChain);
            }

            const HRESULT hr = g_originalPresent ? g_originalPresent(swapChain, syncInterval, flags) : E_FAIL;

            if (isRealPresent && wantsGenerated)
            {
                if (SUCCEEDED(hr) && prepared)
                    ScheduleGeneratedFrame(prepared, Clock::now());
                else
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
            }
            return hr;
        }

        bool ResolvePresentAddress(void** outAddress)
        {
            if (!outAddress) return false;
            *outAddress = nullptr;
            const wchar_t* className = L"RimFG_DummyDX11Window";
            WNDCLASSEXW wc{};
            wc.cbSize = sizeof(wc); wc.lpfnWndProc = DummyWndProc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = className;
            const ATOM atom = RegisterClassExW(&wc);
            if (!atom && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
            HWND hwnd = CreateWindowExW(0, className, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
            if (!hwnd) return false;

            DXGI_SWAP_CHAIN_DESC desc{};
            desc.BufferCount = 1; desc.BufferDesc.Width = 64; desc.BufferDesc.Height = 64; desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.OutputWindow = hwnd; desc.SampleDesc.Count = 1; desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
            D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_0 }, created{};
            ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; ComPtr<IDXGISwapChain> chain;
            const HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, requested, 1, D3D11_SDK_VERSION, &desc, &chain, &device, &created, &context);
            if (SUCCEEDED(hr) && chain)
            {
                void** vtable = *reinterpret_cast<void***>(chain.Get());
                *outAddress = vtable[8];
            }
            DestroyWindow(hwnd); UnregisterClassW(className, wc.hInstance);
            return *outAddress != nullptr;
        }
    }

    bool Initialize(ID3D11Device* unityDevice)
    {
        if (unityDevice) g_unityDevice = unityDevice;
        if (g_installed.load(std::memory_order_acquire)) return true;
        void* presentAddress = nullptr;
        if (!ResolvePresentAddress(&presentAddress)) return false;
        const MH_STATUS init = MH_Initialize();
        if (init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED) return false;
        if (MH_CreateHook(presentAddress, &HookPresent, reinterpret_cast<void**>(&g_originalPresent)) != MH_OK) { MH_Uninitialize(); return false; }
        if (MH_EnableHook(presentAddress) != MH_OK) { MH_RemoveHook(presentAddress); MH_Uninitialize(); g_originalPresent = nullptr; return false; }

        {
            std::lock_guard<std::mutex> lock(g_presenterMutex);
            g_presenterStop = false;
            g_scheduledSequence = 0;
        }
        g_presenterThread = std::thread(PresenterMain);
        g_installed.store(true, std::memory_order_release);
        return true;
    }

    void Shutdown()
    {
        {
            std::lock_guard<std::mutex> lock(g_presenterMutex);
            g_presenterStop = true;
            g_scheduledSequence = 0;
        }
        g_presenterCv.notify_all();
        if (g_presenterThread.joinable()) g_presenterThread.join();

        ClearGeneratedFrameSource();
        ReleaseAsyncResources();
        g_generationCallback.store(nullptr, std::memory_order_release);
        if (g_installed.exchange(false, std::memory_order_acq_rel)) { MH_DisableHook(MH_ALL_HOOKS); MH_Uninitialize(); }
        g_originalPresent = nullptr; g_unitySwapChain.store(nullptr, std::memory_order_release); g_unityDevice = nullptr;
    }

    bool IsInstalled() { return g_installed.load(std::memory_order_acquire); }
    bool HasUnitySwapChain() { return g_unitySwapChain.load(std::memory_order_acquire) != nullptr; }
    IDXGISwapChain* GetUnitySwapChain() { return g_unitySwapChain.load(std::memory_order_acquire); }
    void SetPresentMode(PresentMode mode)
    {
        g_presentMode.store(static_cast<int>(mode), std::memory_order_release);
        if (mode == PresentMode::Disabled)
        {
            std::lock_guard<std::mutex> lock(g_presenterMutex);
            g_scheduledSequence = 0;
            g_generationQuota = 0.0;
            g_presenterCv.notify_one();
        }
    }
    PresentMode GetPresentMode() { return static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire)); }
    void SetBackbufferGenerationCallback(BackbufferGenerationCallback callback) { g_generationCallback.store(callback, std::memory_order_release); }

    void SetGeneratedFrameSource(ID3D11Texture2D* generatedFrame, int width, int height, const HudRectPx* hudRects, int hudRectCount, std::uint32_t frameIndex)
    {
        if (!generatedFrame || width <= 0 || height <= 0) { ClearGeneratedFrameSource(); return; }
        const std::uint32_t next = g_sourceSequence.load(std::memory_order_relaxed) + 1u;
        auto& slot = g_sourceSlots[next & 1u];
        slot.texture = generatedFrame; slot.width = width; slot.height = height; slot.frameIndex = frameIndex;
        slot.hudCount = std::max(0, std::min(hudRectCount, kMaxHudRects));
        if (hudRects) for (int i = 0; i < slot.hudCount; ++i) slot.hud[static_cast<std::size_t>(i)] = hudRects[i];
        g_sourceSequence.store(next, std::memory_order_release); g_sourceAvailable.store(true, std::memory_order_release);
    }

    void ClearGeneratedFrameSource()
    {
        g_sourceAvailable.store(false, std::memory_order_release);
        const std::uint32_t next = g_sourceSequence.load(std::memory_order_relaxed) + 1u;
        g_sourceSlots[next & 1u] = GeneratedSourceSlot{};
        g_sourceSequence.store(next, std::memory_order_release);
    }

    std::uint64_t GeneratedPresentCount() { return g_generatedPresentCount.load(std::memory_order_acquire); }
    std::uint64_t SkippedPresentCount() { return g_skippedPresentCount.load(std::memory_order_acquire); }
}

extern "C" __declspec(dllexport) int __cdecl RimFG_StartPresentHook() { return RimFGPresent::Initialize(nullptr) ? 1 : 0; }
extern "C" __declspec(dllexport) int __cdecl RimFG_HasUnitySwapChain() { return RimFGPresent::HasUnitySwapChain() ? 1 : 0; }
extern "C" __declspec(dllexport) void __cdecl RimFG_StopPresentHook() { RimFGPresent::Shutdown(); }
extern "C" __declspec(dllexport) void __cdecl RimFG_SetPresentMode(int mode)
{
    if (mode < 0 || mode > 2) mode = 0;
    RimFGPresent::SetPresentMode(static_cast<RimFGPresent::PresentMode>(mode));
}
extern "C" __declspec(dllexport) int __cdecl RimFG_GetPresentMode() { return static_cast<int>(RimFGPresent::GetPresentMode()); }
extern "C" __declspec(dllexport) void __cdecl RimFG_SetTargetOutputFps(int fps)
{
    g_targetOutputFps.store(std::max(15, std::min(240, fps)), std::memory_order_release);
}
extern "C" __declspec(dllexport) int __cdecl RimFG_GetTargetOutputFps() { return g_targetOutputFps.load(std::memory_order_acquire); }
extern "C" __declspec(dllexport) double __cdecl RimFG_GetEstimatedBaseFps()
{
    return 1.0 / std::max(1.0 / 240.0, g_realIntervalSeconds);
}
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetGeneratedPresentCount() { return RimFGPresent::GeneratedPresentCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetSkippedPresentCount() { return RimFGPresent::SkippedPresentCount(); }
