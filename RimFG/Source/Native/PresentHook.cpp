#include "PresentHook.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <thread>
#include <windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dcomp.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <dxgi1_3.h>
#include <dxgi1_4.h>
#include <MinHook.h>
#include <wrl/client.h>

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

using Microsoft::WRL::ComPtr;

namespace RimFGPresent
{
    namespace
    {
        using PresentFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);
        using Clock = std::chrono::steady_clock;
        constexpr std::size_t kRealRingSize = 6;

        struct GeneratedSourceSlot
        {
            int width = 0;
            int height = 0;
            int hudCount = 0;
            std::uint32_t frameIndex = 0;
            std::array<HudRectPx, MaxHudRects> hud{};
        };

        struct RealFrameSlot
        {
            ComPtr<ID3D11Device> device;
            ComPtr<ID3D11DeviceContext> context;
            ComPtr<ID3D11Texture2D> snapshot;
            ComPtr<ID3D11Texture2D> composite;
            UINT width = 0;
            UINT height = 0;
            DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
            std::uint64_t sequence = 0;
            Clock::time_point capturedAt{};
            Clock::time_point publishedAt{};
            HWND sourceWindow = nullptr;
            GeneratedSourceSlot source{};
        };

        std::atomic<bool> g_installed{false};
        std::atomic<IDXGISwapChain*> g_unitySwapChain{nullptr};
        std::atomic<IDXGISwapChain*> g_outputSwapChainRaw{nullptr};
        std::atomic<int> g_presentMode{static_cast<int>(PresentMode::ImmediateValidation)};
        std::atomic<BackbufferGenerationCallback> g_captureCallback{nullptr};
        std::atomic<PredictionGenerationCallback> g_predictionCallback{nullptr};
        std::atomic<int> g_targetOutputFps{60};
        ID3D11Device* g_unityDevice = nullptr;
        PresentFn g_originalPresent = nullptr;

        alignas(64) std::array<GeneratedSourceSlot, 2> g_sourceSlots{};
        std::atomic<std::uint32_t> g_sourceSequence{0};
        std::atomic<bool> g_sourceAvailable{false};

        std::atomic<std::uint64_t> g_realPresentCount{0};
        std::atomic<std::uint64_t> g_generatedPresentCount{0};
        std::atomic<std::uint64_t> g_skippedPresentCount{0};
        std::atomic<std::uint64_t> g_frameLatencyTimeoutCount{0};
        std::atomic<std::uint64_t> g_presentFailureCount{0};
        std::atomic<std::uint64_t> g_stalePredictionCount{0};
        std::atomic<std::uint64_t> g_ringBusyDropCount{0};
        std::atomic<std::uint64_t> g_compositionFailureCount{0};

        std::array<RealFrameSlot, kRealRingSize> g_realSlots{};
        ComPtr<ID3D11Device> g_captureDevice;
        ComPtr<ID3D11DeviceContext> g_captureContext;
        ComPtr<ID3D11Multithread> g_multithread;
        UINT g_captureWidth = 0;
        UINT g_captureHeight = 0;
        DXGI_FORMAT g_captureFormat = DXGI_FORMAT_UNKNOWN;

        std::thread g_presenterThread;
        std::mutex g_presenterMutex;
        std::condition_variable g_presenterCv;
        std::atomic<bool> g_presenterStop{false};
        std::uint64_t g_captureSequence = 0;
        std::atomic<std::uint64_t> g_publishedSequence{0};
        std::atomic<std::uint64_t> g_presenterReadingSequence{0};
        std::atomic<double> g_realIntervalSeconds{1.0 / 30.0};
        std::atomic<double> g_outputIntervalSeconds{0.0};
        Clock::time_point g_lastRealPresent{};
        Clock::time_point g_lastOutputPresent{};

        ComPtr<ID3D11Device> g_outputDevice;
        ComPtr<IDXGISwapChain1> g_outputSwapChain;
        ComPtr<IDXGISwapChain2> g_outputSwapChain2;
        ComPtr<IDXGISwapChain3> g_outputSwapChain3;
        ComPtr<IDCompositionDevice> g_dcompDevice;
        ComPtr<IDCompositionTarget> g_dcompTarget;
        ComPtr<IDCompositionVisual> g_dcompVisual;
        HANDLE g_frameLatencyWaitable = nullptr;
        HANDLE g_pacingTimer = nullptr;
        HWND g_compositionOwner = nullptr;
        UINT g_outputWidth = 0;
        UINT g_outputHeight = 0;
        DXGI_FORMAT g_outputFormat = DXGI_FORMAT_UNKNOWN;
        std::atomic<bool> g_presenterReady{false};
        bool g_compositionVisible = false;
        std::atomic<int> g_monitorRefreshHz{60};

        LRESULT CALLBACK DummyWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
        {
            return DefWindowProcW(hwnd, msg, wp, lp);
        }

        bool IsCurrentProcessWindow(HWND hwnd)
        {
            if (!hwnd || !IsWindow(hwnd)) return false;
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            if (pid != GetCurrentProcessId()) return false;
            RECT rc{};
            if (!GetClientRect(hwnd, &rc)) return false;
            return (rc.right - rc.left) >= 320 && (rc.bottom - rc.top) >= 240;
        }

        bool IsSourceWindowUsable(HWND hwnd)
        {
            return IsCurrentProcessWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd);
        }

        bool IsTargetSwapChain(IDXGISwapChain* swapChain)
        {
            if (!swapChain || swapChain == g_outputSwapChainRaw.load(std::memory_order_acquire)) return false;
            DXGI_SWAP_CHAIN_DESC desc{};
            if (FAILED(swapChain->GetDesc(&desc)) || !IsCurrentProcessWindow(desc.OutputWindow)) return false;
            if (g_unityDevice)
            {
                ComPtr<ID3D11Device> device;
                if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(device.GetAddressOf()))) || device.Get() != g_unityDevice)
                    return false;
            }
            return true;
        }

        GeneratedSourceSlot ReadLatestSource()
        {
            if (!g_sourceAvailable.load(std::memory_order_acquire)) return GeneratedSourceSlot{};
            const std::uint32_t seq = g_sourceSequence.load(std::memory_order_acquire);
            return g_sourceSlots[seq & 1u];
        }

        bool GetCurrentBackBuffer(IDXGISwapChain* chain, ComPtr<ID3D11Texture2D>& out)
        {
            out.Reset();
            if (!chain) return false;
            UINT index = 0;
            ComPtr<IDXGISwapChain3> chain3;
            if (SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(chain3.GetAddressOf()))) && chain3)
                index = chain3->GetCurrentBackBufferIndex();
            if (SUCCEEDED(chain->GetBuffer(index, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(out.GetAddressOf()))) && out)
                return true;
            out.Reset();
            return index != 0 && SUCCEEDED(chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(out.GetAddressOf()))) && out;
        }

        bool GetCurrentOutputBackBuffer(ComPtr<ID3D11Texture2D>& out)
        {
            out.Reset();
            if (!g_outputSwapChain) return false;
            const UINT index = g_outputSwapChain3 ? g_outputSwapChain3->GetCurrentBackBufferIndex() : 0;
            if (SUCCEEDED(g_outputSwapChain->GetBuffer(index, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(out.GetAddressOf()))) && out)
                return true;
            out.Reset();
            return index != 0 && SUCCEEDED(g_outputSwapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(out.GetAddressOf()))) && out;
        }

        bool SameCopyFamily(DXGI_FORMAT a, DXGI_FORMAT b)
        {
            if (a == b) return true;
            const bool rgbaA = a == DXGI_FORMAT_R8G8B8A8_UNORM || a == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
            const bool rgbaB = b == DXGI_FORMAT_R8G8B8A8_UNORM || b == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
            if (rgbaA && rgbaB) return true;
            const bool bgraA = a == DXGI_FORMAT_B8G8R8A8_UNORM || a == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
            const bool bgraB = b == DXGI_FORMAT_B8G8R8A8_UNORM || b == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
            return bgraA && bgraB;
        }

        DXGI_FORMAT OutputCompatibleFormat(DXGI_FORMAT format)
        {
            switch (format)
            {
            case DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_B8G8R8A8_UNORM:
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
            case DXGI_FORMAT_R10G10B10A2_UNORM:
            case DXGI_FORMAT_R16G16B16A16_FLOAT: return format;
            default: return DXGI_FORMAT_UNKNOWN;
            }
        }

        void CopyProtectedRects(ID3D11DeviceContext* context, ID3D11Texture2D* realFrame, ID3D11Texture2D* composite,
            const GeneratedSourceSlot& source, UINT width, UINT height)
        {
            if (!context || !realFrame || !composite) return;
            for (int i = 0; i < source.hudCount; ++i)
            {
                const HudRectPx& r = source.hud[static_cast<std::size_t>(i)];
                const LONG left = std::max<LONG>(0, static_cast<LONG>(std::floor(r.x)));
                const LONG top = std::max<LONG>(0, static_cast<LONG>(std::floor(r.y)));
                const LONG right = std::min<LONG>(static_cast<LONG>(width), static_cast<LONG>(std::ceil(r.x + r.width)));
                const LONG bottom = std::min<LONG>(static_cast<LONG>(height), static_cast<LONG>(std::ceil(r.y + r.height)));
                if (right <= left || bottom <= top) continue;
                D3D11_BOX box{static_cast<UINT>(left), static_cast<UINT>(top), 0, static_cast<UINT>(right), static_cast<UINT>(bottom), 1};
                context->CopySubresourceRegion(composite, 0, static_cast<UINT>(left), static_cast<UINT>(top), 0, realFrame, 0, &box);
            }
        }

        void ResetRealSlot(RealFrameSlot& slot)
        {
            slot.device.Reset(); slot.context.Reset(); slot.snapshot.Reset(); slot.composite.Reset();
            slot.width = slot.height = 0; slot.format = DXGI_FORMAT_UNKNOWN; slot.sequence = 0;
            slot.capturedAt = Clock::time_point{}; slot.publishedAt = Clock::time_point{};
            slot.sourceWindow = nullptr; slot.source = GeneratedSourceSlot{};
        }

        void ReleaseCaptureResources()
        {
            for (RealFrameSlot& slot : g_realSlots) ResetRealSlot(slot);
            g_multithread.Reset(); g_captureContext.Reset(); g_captureDevice.Reset();
            g_captureWidth = g_captureHeight = 0; g_captureFormat = DXGI_FORMAT_UNKNOWN;
        }

        bool EnsureCaptureResources(ID3D11Device* device, ID3D11Texture2D* backBuffer)
        {
            if (!device || !backBuffer) return false;
            D3D11_TEXTURE2D_DESC desc{}; backBuffer->GetDesc(&desc);
            if (!desc.Width || !desc.Height || desc.SampleDesc.Count != 1) return false;

            bool complete = g_captureDevice.Get() == device && g_captureContext &&
                g_captureWidth == desc.Width && g_captureHeight == desc.Height && g_captureFormat == desc.Format;
            if (complete)
                for (const RealFrameSlot& slot : g_realSlots) complete = complete && slot.snapshot && slot.composite;
            if (complete) return true;

            ReleaseCaptureResources();
            g_captureDevice = device;
            device->GetImmediateContext(&g_captureContext);
            if (!g_captureContext) return false;
            g_captureContext.As(&g_multithread);
            if (g_multithread) g_multithread->SetMultithreadProtected(TRUE);

            D3D11_TEXTURE2D_DESC copy = desc;
            copy.MipLevels = 1; copy.ArraySize = 1; copy.Usage = D3D11_USAGE_DEFAULT;
            copy.BindFlags = 0; copy.CPUAccessFlags = 0; copy.MiscFlags = 0;
            for (RealFrameSlot& slot : g_realSlots)
            {
                if (FAILED(device->CreateTexture2D(&copy, nullptr, &slot.snapshot)) ||
                    FAILED(device->CreateTexture2D(&copy, nullptr, &slot.composite)))
                {
                    ReleaseCaptureResources();
                    return false;
                }
            }
            g_captureWidth = desc.Width; g_captureHeight = desc.Height; g_captureFormat = desc.Format;
            return true;
        }

        int QueryMonitorRefreshHz(HWND owner)
        {
            if (!owner) return 60;
            MONITORINFOEXW mi{}; mi.cbSize = sizeof(mi);
            if (!GetMonitorInfoW(MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST), &mi)) return 60;
            DEVMODEW dm{}; dm.dmSize = sizeof(dm);
            if (!EnumDisplaySettingsW(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm)) return 60;
            const int hz = static_cast<int>(dm.dmDisplayFrequency);
            return hz >= 24 && hz <= 1000 ? hz : 60;
        }

        void ReleaseCompositionResources()
        {
            g_presenterReady.store(false, std::memory_order_release);
            g_outputSwapChainRaw.store(nullptr, std::memory_order_release);
            g_frameLatencyWaitable = nullptr;
            if (g_dcompVisual) g_dcompVisual->SetContent(nullptr);
            if (g_dcompTarget) g_dcompTarget->SetRoot(nullptr);
            if (g_dcompDevice) g_dcompDevice->Commit();
            g_outputSwapChain3.Reset(); g_outputSwapChain2.Reset(); g_outputSwapChain.Reset();
            g_dcompVisual.Reset(); g_dcompTarget.Reset(); g_dcompDevice.Reset(); g_outputDevice.Reset();
            g_compositionOwner = nullptr; g_outputWidth = g_outputHeight = 0; g_outputFormat = DXGI_FORMAT_UNKNOWN;
            g_compositionVisible = false;
        }

        bool SetCompositionVisible(bool visible)
        {
            if (!g_dcompVisual || !g_dcompDevice) return false;
            if (g_compositionVisible == visible) return true;
            const HRESULT contentHr = g_dcompVisual->SetContent(visible ? g_outputSwapChain.Get() : nullptr);
            if (FAILED(contentHr) || FAILED(g_dcompDevice->Commit()))
            {
                g_compositionFailureCount.fetch_add(1, std::memory_order_relaxed);
                return false;
            }
            g_compositionVisible = visible;
            return true;
        }

        bool TryCreateCompositionSwapChain(IDXGIFactory2* factory, ID3D11Device* device, DXGI_SWAP_CHAIN_DESC1& desc)
        {
            g_outputSwapChain.Reset();
            return factory && device && SUCCEEDED(factory->CreateSwapChainForComposition(device, &desc, nullptr, &g_outputSwapChain)) && g_outputSwapChain;
        }

        bool CreateCompositionOutput(const RealFrameSlot& slot)
        {
            if (!slot.device || !slot.context || !slot.sourceWindow || !IsWindow(slot.sourceWindow)) return false;
            const DXGI_FORMAT wanted = OutputCompatibleFormat(slot.format);
            if (wanted == DXGI_FORMAT_UNKNOWN) return false;
            ReleaseCompositionResources();

            ComPtr<IDXGIDevice> dxgiDevice; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIFactory2> factory;
            if (FAILED(slot.device.As(&dxgiDevice)) || !dxgiDevice || FAILED(dxgiDevice->GetAdapter(&adapter)) || !adapter ||
                FAILED(adapter->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(factory.GetAddressOf()))) || !factory)
                return false;

            if (FAILED(DCompositionCreateDevice(dxgiDevice.Get(), __uuidof(IDCompositionDevice), reinterpret_cast<void**>(g_dcompDevice.GetAddressOf()))) || !g_dcompDevice ||
                FAILED(g_dcompDevice->CreateTargetForHwnd(slot.sourceWindow, TRUE, &g_dcompTarget)) || !g_dcompTarget ||
                FAILED(g_dcompDevice->CreateVisual(&g_dcompVisual)) || !g_dcompVisual)
            {
                g_compositionFailureCount.fetch_add(1, std::memory_order_relaxed);
                ReleaseCompositionResources();
                return false;
            }

            DXGI_SWAP_CHAIN_DESC1 desc{};
            desc.Width = slot.width; desc.Height = slot.height; desc.Format = wanted; desc.Stereo = FALSE;
            desc.SampleDesc.Count = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
            desc.Scaling = DXGI_SCALING_STRETCH; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE; desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

            bool created = TryCreateCompositionSwapChain(factory.Get(), slot.device.Get(), desc);
            if (!created)
            {
                desc.Flags = 0;
                created = TryCreateCompositionSwapChain(factory.Get(), slot.device.Get(), desc);
            }
            if (!created)
            {
                desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
                desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
                created = TryCreateCompositionSwapChain(factory.Get(), slot.device.Get(), desc);
            }
            if (!created)
            {
                desc.Flags = 0;
                created = TryCreateCompositionSwapChain(factory.Get(), slot.device.Get(), desc);
            }
            if (!created)
            {
                g_compositionFailureCount.fetch_add(1, std::memory_order_relaxed);
                ReleaseCompositionResources();
                return false;
            }

            g_outputSwapChain.As(&g_outputSwapChain2);
            g_outputSwapChain.As(&g_outputSwapChain3);
            if (g_outputSwapChain2 && (desc.Flags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) != 0)
            {
                g_outputSwapChain2->SetMaximumFrameLatency(1);
                g_frameLatencyWaitable = g_outputSwapChain2->GetFrameLatencyWaitableObject();
            }

            if (FAILED(g_dcompVisual->SetContent(nullptr)) || FAILED(g_dcompTarget->SetRoot(g_dcompVisual.Get())) || FAILED(g_dcompDevice->Commit()))
            {
                g_compositionFailureCount.fetch_add(1, std::memory_order_relaxed);
                ReleaseCompositionResources();
                return false;
            }

            g_outputDevice = slot.device; g_outputWidth = slot.width; g_outputHeight = slot.height;
            g_outputFormat = wanted; g_compositionOwner = slot.sourceWindow; g_compositionVisible = false;
            g_monitorRefreshHz.store(QueryMonitorRefreshHz(slot.sourceWindow), std::memory_order_release);
            g_outputSwapChainRaw.store(g_outputSwapChain.Get(), std::memory_order_release);
            g_presenterReady.store(true, std::memory_order_release);
            return true;
        }

        bool EnsureCompositionOutput(const RealFrameSlot& slot)
        {
            const DXGI_FORMAT wanted = OutputCompatibleFormat(slot.format);
            if (wanted == DXGI_FORMAT_UNKNOWN) return false;
            if (g_presenterReady.load(std::memory_order_acquire) && g_outputSwapChain && g_outputDevice.Get() == slot.device.Get() &&
                g_compositionOwner == slot.sourceWindow && g_outputWidth == slot.width && g_outputHeight == slot.height && g_outputFormat == wanted)
                return true;
            return CreateCompositionOutput(slot);
        }

        void WaitUntilDeadline(Clock::time_point deadline)
        {
            const Clock::time_point now = Clock::now();
            if (deadline <= now) return;
            if (!g_pacingTimer)
                g_pacingTimer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (!g_pacingTimer)
            {
                std::this_thread::sleep_until(deadline);
                return;
            }
            const auto ns = std::chrono::duration_cast<std::chrono::nanoseconds>(deadline - now).count();
            LARGE_INTEGER due{}; due.QuadPart = -std::max<LONGLONG>(1, static_cast<LONGLONG>(ns / 100));
            if (!SetWaitableTimerEx(g_pacingTimer, &due, 0, nullptr, nullptr, nullptr, 0))
            {
                std::this_thread::sleep_until(deadline);
                return;
            }
            WaitForSingleObject(g_pacingTimer, 1000);
        }

        bool FrameLatencyReady()
        {
            if (!g_frameLatencyWaitable) return true;
            const DWORD result = WaitForSingleObject(g_frameLatencyWaitable, 1);
            if (result == WAIT_OBJECT_0 || result == WAIT_ABANDONED) return true;
            g_frameLatencyTimeoutCount.fetch_add(1, std::memory_order_relaxed);
            return false;
        }

        bool CopyToOutputBackBuffer(const RealFrameSlot& slot, ID3D11Texture2D* source)
        {
            if (!source || !slot.context || !g_outputSwapChain) return false;
            ComPtr<ID3D11Texture2D> output;
            if (!GetCurrentOutputBackBuffer(output) || !output) return false;
            D3D11_TEXTURE2D_DESC src{}, dst{}; source->GetDesc(&src); output->GetDesc(&dst);
            if (src.Width != dst.Width || src.Height != dst.Height || src.SampleDesc.Count != 1 || dst.SampleDesc.Count != 1 || !SameCopyFamily(src.Format, dst.Format))
                return false;
            slot.context->CopyResource(output.Get(), source);
            return true;
        }

        bool PresentOutputTexture(const RealFrameSlot& slot, ID3D11Texture2D* source)
        {
            if (!source || !g_outputSwapChain || !CopyToOutputBackBuffer(slot, source)) return false;
            const HRESULT hr = g_outputSwapChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
            {
                g_frameLatencyTimeoutCount.fetch_add(1, std::memory_order_relaxed);
                return false;
            }
            if (FAILED(hr))
            {
                g_presentFailureCount.fetch_add(1, std::memory_order_relaxed);
                if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET) ReleaseCompositionResources();
                return false;
            }
            const Clock::time_point now = Clock::now();
            if (g_lastOutputPresent.time_since_epoch().count() != 0)
            {
                const double dt = std::chrono::duration<double>(now - g_lastOutputPresent).count();
                if (dt > 0.0005 && dt < 0.5)
                {
                    const double old = g_outputIntervalSeconds.load(std::memory_order_relaxed);
                    g_outputIntervalSeconds.store(old <= 0.0 ? dt : old + (dt - old) * 0.12, std::memory_order_relaxed);
                }
            }
            g_lastOutputPresent = now;
            return true;
        }

        bool PresentReal(const RealFrameSlot& slot)
        {
            return slot.snapshot && PresentOutputTexture(slot, slot.snapshot.Get());
        }

        bool PredictionMatchesSlot(const RealFrameSlot& slot)
        {
            if (slot.source.frameIndex == 0) return false;
            const GeneratedSourceSlot latest = ReadLatestSource();
            return latest.frameIndex == slot.source.frameIndex && latest.width == static_cast<int>(slot.width) && latest.height == static_cast<int>(slot.height);
        }

        bool PresentPrediction(const RealFrameSlot& slot, float fraction)
        {
            if (!slot.snapshot || !slot.composite || !slot.context || !slot.device || !PredictionMatchesSlot(slot))
            {
                g_stalePredictionCount.fetch_add(1, std::memory_order_relaxed);
                return false;
            }
            PredictionGenerationCallback predictor = g_predictionCallback.load(std::memory_order_acquire);
            if (!predictor) return false;
            ID3D11Texture2D* predicted = predictor(fraction);
            if (!predicted || g_publishedSequence.load(std::memory_order_acquire) != slot.sequence || !PredictionMatchesSlot(slot))
            {
                g_stalePredictionCount.fetch_add(1, std::memory_order_relaxed);
                return false;
            }
            ComPtr<ID3D11Device> predictedDevice; predicted->GetDevice(&predictedDevice);
            if (!predictedDevice || predictedDevice.Get() != slot.device.Get()) return false;
            D3D11_TEXTURE2D_DESC pd{}; predicted->GetDesc(&pd);
            if (pd.Width != slot.width || pd.Height != slot.height || pd.SampleDesc.Count != 1 || !SameCopyFamily(pd.Format, slot.format)) return false;
            slot.context->CopyResource(slot.composite.Get(), predicted);
            CopyProtectedRects(slot.context.Get(), slot.snapshot.Get(), slot.composite.Get(), slot.source, slot.width, slot.height);
            if (g_publishedSequence.load(std::memory_order_acquire) != slot.sequence || !PredictionMatchesSlot(slot))
            {
                g_stalePredictionCount.fetch_add(1, std::memory_order_relaxed);
                return false;
            }
            return PresentOutputTexture(slot, slot.composite.Get());
        }

        std::uint64_t CaptureRealFrameSlot(IDXGISwapChain* swapChain, Clock::time_point now, bool generatorAdvanced)
        {
            if (!swapChain) return 0;
            ComPtr<ID3D11Device> device;
            if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(device.GetAddressOf()))) || !device) return 0;
            ComPtr<ID3D11Texture2D> backBuffer;
            if (!GetCurrentBackBuffer(swapChain, backBuffer) || !backBuffer) return 0;
            D3D11_TEXTURE2D_DESC bd{}; backBuffer->GetDesc(&bd);
            DXGI_SWAP_CHAIN_DESC sd{};
            if (!bd.Width || !bd.Height || bd.SampleDesc.Count != 1 || FAILED(swapChain->GetDesc(&sd)) || !sd.OutputWindow) return 0;

            std::unique_lock<std::mutex> lock(g_presenterMutex, std::try_to_lock);
            if (!lock.owns_lock())
            {
                g_ringBusyDropCount.fetch_add(1, std::memory_order_relaxed);
                return 0;
            }
            if (!EnsureCaptureResources(device.Get(), backBuffer.Get())) return 0;

            const std::uint64_t next = g_captureSequence + 1u;
            const std::uint64_t reading = g_presenterReadingSequence.load(std::memory_order_acquire);
            if (reading != 0 && (reading % kRealRingSize) == (next % kRealRingSize))
            {
                g_ringBusyDropCount.fetch_add(1, std::memory_order_relaxed);
                return 0;
            }

            g_captureSequence = next;
            RealFrameSlot& slot = g_realSlots[next % kRealRingSize];
            slot.device = device; slot.context = g_captureContext;
            g_captureContext->CopyResource(slot.snapshot.Get(), backBuffer.Get());
            slot.width = bd.Width; slot.height = bd.Height; slot.format = bd.Format; slot.sequence = next;
            slot.capturedAt = now; slot.publishedAt = Clock::time_point{}; slot.sourceWindow = sd.OutputWindow;
            slot.source = generatorAdvanced ? ReadLatestSource() : GeneratedSourceSlot{};
            return next;
        }

        void PublishRealFrame(std::uint64_t sequence, Clock::time_point now)
        {
            if (!sequence) return;
            std::unique_lock<std::mutex> lock(g_presenterMutex, std::try_to_lock);
            if (!lock.owns_lock())
            {
                g_ringBusyDropCount.fetch_add(1, std::memory_order_relaxed);
                return;
            }
            RealFrameSlot& slot = g_realSlots[sequence % kRealRingSize];
            if (slot.sequence != sequence) return;
            slot.publishedAt = now;
            if (g_lastRealPresent.time_since_epoch().count() != 0)
            {
                const double seconds = std::chrono::duration<double>(now - g_lastRealPresent).count();
                if (seconds >= 1.0 / 1000.0 && seconds <= 0.5)
                {
                    const double old = g_realIntervalSeconds.load(std::memory_order_relaxed);
                    g_realIntervalSeconds.store(old + (seconds - old) * 0.20, std::memory_order_relaxed);
                }
            }
            g_lastRealPresent = now;
            g_publishedSequence.store(sequence, std::memory_order_release);
            lock.unlock();
            g_presenterCv.notify_all();
        }

        bool CopyLatestRealSlot(RealFrameSlot& out)
        {
            const std::uint64_t sequence = g_publishedSequence.load(std::memory_order_acquire);
            if (!sequence) return false;
            std::lock_guard<std::mutex> lock(g_presenterMutex);
            const RealFrameSlot& slot = g_realSlots[sequence % kRealRingSize];
            if (slot.sequence != sequence || !slot.snapshot || !slot.context || !slot.device) return false;
            out = slot;
            return true;
        }

        void PresenterMain()
        {
            std::uint64_t lastDisplayedReal = 0;
            Clock::time_point nextOutput{};
            Clock::time_point nextRefreshQuery{};
            int lastEffectiveFps = 0;

            while (!g_presenterStop.load(std::memory_order_acquire))
            {
                const PresentMode mode = static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire));
                RealFrameSlot slot;
                if (mode == PresentMode::Disabled || !CopyLatestRealSlot(slot))
                {
                    if (g_dcompVisual) SetCompositionVisible(false);
                    nextOutput = Clock::time_point{};
                    std::unique_lock<std::mutex> lock(g_presenterMutex);
                    g_presenterCv.wait_for(lock, std::chrono::milliseconds(20));
                    continue;
                }
                if (!IsSourceWindowUsable(slot.sourceWindow))
                {
                    if (g_dcompVisual) SetCompositionVisible(false);
                    nextOutput = Clock::time_point{};
                    std::this_thread::sleep_for(std::chrono::milliseconds(25));
                    continue;
                }

                const double baseFps = 1.0 / std::max(1.0 / 1000.0, g_realIntervalSeconds.load(std::memory_order_relaxed));
                const int targetFps = std::max(1, g_targetOutputFps.load(std::memory_order_acquire));
                if (static_cast<double>(targetFps) <= baseFps + 0.5)
                {
                    if (g_dcompVisual) SetCompositionVisible(false);
                    nextOutput = Clock::time_point{};
                    std::this_thread::sleep_for(std::chrono::milliseconds(4));
                    continue;
                }
                if (!EnsureCompositionOutput(slot))
                {
                    g_compositionFailureCount.fetch_add(1, std::memory_order_relaxed);
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
                    nextOutput = Clock::time_point{};
                    std::this_thread::sleep_for(std::chrono::milliseconds(20));
                    continue;
                }

                const Clock::time_point now = Clock::now();
                if (nextRefreshQuery.time_since_epoch().count() == 0 || now >= nextRefreshQuery)
                {
                    g_monitorRefreshHz.store(QueryMonitorRefreshHz(slot.sourceWindow), std::memory_order_release);
                    nextRefreshQuery = now + std::chrono::seconds(1);
                }
                const int monitorHz = std::max(24, g_monitorRefreshHz.load(std::memory_order_acquire));
                const int effectiveFps = std::max(1, std::min(targetFps, monitorHz));
                const auto period = std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(1.0 / static_cast<double>(effectiveFps)));
                if (nextOutput.time_since_epoch().count() == 0 || effectiveFps != lastEffectiveFps || now > nextOutput + period * 3)
                    nextOutput = now;
                lastEffectiveFps = effectiveFps;

                WaitUntilDeadline(nextOutput);
                nextOutput += period;
                if (g_presenterStop.load(std::memory_order_acquire)) break;
                if (static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire)) == PresentMode::Disabled) continue;
                if (!FrameLatencyReady())
                {
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
                    continue;
                }
                if (!CopyLatestRealSlot(slot) || !EnsureCompositionOutput(slot))
                {
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
                    continue;
                }

                g_presenterReadingSequence.store(slot.sequence, std::memory_order_release);
                bool presented = false;
                if (slot.sequence != lastDisplayedReal)
                {
                    presented = PresentReal(slot);
                    if (presented)
                    {
                        lastDisplayedReal = slot.sequence;
                        g_realPresentCount.fetch_add(1, std::memory_order_relaxed);
                    }
                }
                else if (slot.source.frameIndex != 0)
                {
                    const Clock::time_point basis = slot.publishedAt.time_since_epoch().count() ? slot.publishedAt : slot.capturedAt;
                    const double age = std::max(0.0, std::chrono::duration<double>(Clock::now() - basis).count());
                    const double interval = std::max(1.0 / 1000.0, g_realIntervalSeconds.load(std::memory_order_relaxed));
                    const float fraction = static_cast<float>(std::max(0.02, std::min(0.999999, age / interval)));
                    presented = PresentPrediction(slot, fraction);
                    if (presented) g_generatedPresentCount.fetch_add(1, std::memory_order_relaxed);
                }
                else
                {
                    g_stalePredictionCount.fetch_add(1, std::memory_order_relaxed);
                }
                g_presenterReadingSequence.store(0, std::memory_order_release);

                if (presented) SetCompositionVisible(true);
                else g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);

                const Clock::time_point after = Clock::now();
                if (after > nextOutput + period) nextOutput = after + period;
            }

            g_presenterReadingSequence.store(0, std::memory_order_release);
            if (g_dcompVisual) SetCompositionVisible(false);
            ReleaseCompositionResources();
            if (g_pacingTimer) { CloseHandle(g_pacingTimer); g_pacingTimer = nullptr; }
        }

        HRESULT __stdcall HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
        {
            const bool target = IsTargetSwapChain(swapChain);
            std::uint64_t captured = 0;
            PresentMode mode = PresentMode::Disabled;
            if (target)
            {
                g_unitySwapChain.store(swapChain, std::memory_order_release);
                mode = static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire));
                bool generatorAdvanced = false;
                if ((flags & DXGI_PRESENT_TEST) == 0)
                {
                    BackbufferGenerationCallback capture = g_captureCallback.load(std::memory_order_acquire);
                    ComPtr<ID3D11Texture2D> backBuffer;
                    if (GetCurrentBackBuffer(swapChain, backBuffer) && backBuffer)
                    {
                        if (capture) generatorAdvanced = capture(backBuffer.Get());
                        if (mode != PresentMode::Disabled)
                            captured = CaptureRealFrameSlot(swapChain, Clock::now(), generatorAdvanced);
                    }
                }
            }

            const HRESULT hr = g_originalPresent ? g_originalPresent(swapChain, syncInterval, flags) : E_FAIL;
            if (target && mode != PresentMode::Disabled && (flags & DXGI_PRESENT_TEST) == 0 && SUCCEEDED(hr) && captured)
                PublishRealFrame(captured, Clock::now());
            else if (target && mode != PresentMode::Disabled && (flags & DXGI_PRESENT_TEST) == 0 && !captured)
                g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
            return hr;
        }

        bool ResolvePresentAddress(void** outAddress)
        {
            if (!outAddress) return false;
            *outAddress = nullptr;
            const wchar_t* className = L"RimFG_DummyDX11Window";
            WNDCLASSEXW wc{}; wc.cbSize = sizeof(wc); wc.lpfnWndProc = DummyWndProc;
            wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = className;
            const ATOM atom = RegisterClassExW(&wc);
            if (!atom && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
            HWND hwnd = CreateWindowExW(0, className, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
            if (!hwnd) return false;

            DXGI_SWAP_CHAIN_DESC desc{};
            desc.BufferCount = 1; desc.BufferDesc.Width = 64; desc.BufferDesc.Height = 64;
            desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.OutputWindow = hwnd; desc.SampleDesc.Count = 1; desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
            D3D_FEATURE_LEVEL requested[] = {D3D_FEATURE_LEVEL_11_0}, created{};
            ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; ComPtr<IDXGISwapChain> chain;
            const HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, requested, 1,
                D3D11_SDK_VERSION, &desc, &chain, &device, &created, &context);
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
        if (MH_CreateHook(presentAddress, &HookPresent, reinterpret_cast<void**>(&g_originalPresent)) != MH_OK) return false;
        if (MH_EnableHook(presentAddress) != MH_OK) { MH_RemoveHook(presentAddress); g_originalPresent = nullptr; return false; }

        g_presenterStop.store(false, std::memory_order_release);
        g_captureSequence = 0; g_publishedSequence.store(0, std::memory_order_release);
        g_presenterReadingSequence.store(0, std::memory_order_release);
        g_lastRealPresent = Clock::time_point{}; g_lastOutputPresent = Clock::time_point{};
        g_realIntervalSeconds.store(1.0 / 30.0, std::memory_order_release);
        g_outputIntervalSeconds.store(0.0, std::memory_order_release);
        g_presenterThread = std::thread(PresenterMain);
        g_installed.store(true, std::memory_order_release);
        return true;
    }

    void Shutdown()
    {
        g_presenterStop.store(true, std::memory_order_release);
        g_presenterCv.notify_all();
        if (g_presenterThread.joinable()) g_presenterThread.join();
        ClearGeneratedFrameSource();
        ReleaseCaptureResources();
        g_captureCallback.store(nullptr, std::memory_order_release);
        g_predictionCallback.store(nullptr, std::memory_order_release);
        if (g_installed.exchange(false, std::memory_order_acq_rel))
        {
            MH_DisableHook(MH_ALL_HOOKS);
            MH_Uninitialize();
        }
        g_originalPresent = nullptr; g_unitySwapChain.store(nullptr, std::memory_order_release); g_unityDevice = nullptr;
    }

    bool IsInstalled() { return g_installed.load(std::memory_order_acquire); }
    bool HasUnitySwapChain() { return g_unitySwapChain.load(std::memory_order_acquire) != nullptr; }
    IDXGISwapChain* GetUnitySwapChain() { return g_unitySwapChain.load(std::memory_order_acquire); }

    void SetPresentMode(PresentMode mode)
    {
        if (mode == PresentMode::VSync2x) mode = PresentMode::ImmediateValidation;
        g_presentMode.store(static_cast<int>(mode), std::memory_order_release);
        g_presenterCv.notify_all();
    }
    PresentMode GetPresentMode() { return static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire)); }
    void SetBackbufferGenerationCallback(BackbufferGenerationCallback cb) { g_captureCallback.store(cb, std::memory_order_release); }
    void SetPredictionGenerationCallback(PredictionGenerationCallback cb) { g_predictionCallback.store(cb, std::memory_order_release); }

    void SetGeneratedFrameSource(ID3D11Texture2D* generatedFrame, int width, int height, const HudRectPx* rects, int count, std::uint32_t frameIndex)
    {
        if (!generatedFrame || width <= 0 || height <= 0) { ClearGeneratedFrameSource(); return; }
        const std::uint32_t next = g_sourceSequence.load(std::memory_order_relaxed) + 1u;
        GeneratedSourceSlot& slot = g_sourceSlots[next & 1u];
        slot.width = width; slot.height = height; slot.frameIndex = frameIndex;
        slot.hudCount = std::max(0, std::min(count, MaxHudRects));
        if (rects) for (int i = 0; i < slot.hudCount; ++i) slot.hud[static_cast<std::size_t>(i)] = rects[i];
        g_sourceSequence.store(next, std::memory_order_release);
        g_sourceAvailable.store(true, std::memory_order_release);
    }

    void ClearGeneratedFrameSource()
    {
        g_sourceAvailable.store(false, std::memory_order_release);
        const std::uint32_t next = g_sourceSequence.load(std::memory_order_relaxed) + 1u;
        g_sourceSlots[next & 1u] = GeneratedSourceSlot{};
        g_sourceSequence.store(next, std::memory_order_release);
    }

    void SetTargetOutputFps(int fps) { g_targetOutputFps.store(std::max(1, std::min(1000000000, fps)), std::memory_order_release); g_presenterCv.notify_all(); }
    int GetTargetOutputFps() { return g_targetOutputFps.load(std::memory_order_acquire); }
    double EstimatedBaseFps() { return 1.0 / std::max(1.0 / 1000.0, g_realIntervalSeconds.load(std::memory_order_acquire)); }
    double EstimatedOutputFps() { const double v = g_outputIntervalSeconds.load(std::memory_order_acquire); return v > 0.0005 ? 1.0 / v : 0.0; }
    bool PresenterReady() { return g_presenterReady.load(std::memory_order_acquire); }
    int MonitorRefreshHz() { return g_monitorRefreshHz.load(std::memory_order_acquire); }
    std::uint64_t RealPresentCount() { return g_realPresentCount.load(std::memory_order_acquire); }
    std::uint64_t GeneratedPresentCount() { return g_generatedPresentCount.load(std::memory_order_acquire); }
    std::uint64_t SkippedPresentCount() { return g_skippedPresentCount.load(std::memory_order_acquire); }
    std::uint64_t FrameLatencyTimeoutCount() { return g_frameLatencyTimeoutCount.load(std::memory_order_acquire); }
    std::uint64_t PresentFailureCount() { return g_presentFailureCount.load(std::memory_order_acquire); }
    std::uint64_t StalePredictionCount() { return g_stalePredictionCount.load(std::memory_order_acquire); }
    std::uint64_t RingBusyDropCount() { return g_ringBusyDropCount.load(std::memory_order_acquire); }
    std::uint64_t CompositionFailureCount() { return g_compositionFailureCount.load(std::memory_order_acquire); }
}

extern "C" __declspec(dllexport) int __cdecl RimFG_StartPresentHook() { return RimFGPresent::Initialize(nullptr) ? 1 : 0; }
extern "C" __declspec(dllexport) int __cdecl RimFG_HasUnitySwapChain() { return RimFGPresent::HasUnitySwapChain() ? 1 : 0; }
extern "C" __declspec(dllexport) void __cdecl RimFG_StopPresentHook() { RimFGPresent::Shutdown(); }
extern "C" __declspec(dllexport) void __cdecl RimFG_SetPresentMode(int mode) { if (mode < 0 || mode > 2) mode = 0; RimFGPresent::SetPresentMode(static_cast<RimFGPresent::PresentMode>(mode)); }
extern "C" __declspec(dllexport) int __cdecl RimFG_GetPresentMode() { return static_cast<int>(RimFGPresent::GetPresentMode()); }
extern "C" __declspec(dllexport) void __cdecl RimFG_SetTargetOutputFps(int fps) { RimFGPresent::SetTargetOutputFps(fps); }
extern "C" __declspec(dllexport) int __cdecl RimFG_GetTargetOutputFps() { return RimFGPresent::GetTargetOutputFps(); }
extern "C" __declspec(dllexport) double __cdecl RimFG_GetEstimatedBaseFps() { return RimFGPresent::EstimatedBaseFps(); }
extern "C" __declspec(dllexport) double __cdecl RimFG_GetEstimatedOutputFps() { return RimFGPresent::EstimatedOutputFps(); }
extern "C" __declspec(dllexport) int __cdecl RimFG_IsPresenterReady() { return RimFGPresent::PresenterReady() ? 1 : 0; }
extern "C" __declspec(dllexport) int __cdecl RimFG_GetMonitorRefreshHz() { return RimFGPresent::MonitorRefreshHz(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetRealPresentCount() { return RimFGPresent::RealPresentCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetGeneratedPresentCount() { return RimFGPresent::GeneratedPresentCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetSkippedPresentCount() { return RimFGPresent::SkippedPresentCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetFrameLatencyTimeoutCount() { return RimFGPresent::FrameLatencyTimeoutCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetPresentFailureCount() { return RimFGPresent::PresentFailureCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetStalePredictionCount() { return RimFGPresent::StalePredictionCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetRingBusyDropCount() { return RimFGPresent::RingBusyDropCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetCompositionFailureCount() { return RimFGPresent::CompositionFailureCount(); }
