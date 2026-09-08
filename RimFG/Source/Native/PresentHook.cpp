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
        constexpr std::size_t kOpenedCacheSize = static_cast<std::size_t>(MaxBatchFrames * 2);

        struct OpenedSharedFrame
        {
            HANDLE handle = nullptr;
            ComPtr<ID3D11Texture2D> texture;
            ComPtr<IDXGIKeyedMutex> keyedMutex;
        };

        std::atomic<bool> g_installed{false};
        std::atomic<IDXGISwapChain*> g_unitySwapChain{nullptr};
        std::atomic<IDXGISwapChain*> g_outputSwapChainRaw{nullptr};
        std::atomic<int> g_presentMode{static_cast<int>(PresentMode::ImmediateValidation)};
        std::atomic<BackbufferGenerationCallback> g_captureCallback{nullptr};
        std::atomic<int> g_targetOutputFps{60};
        ID3D11Device* g_unityDevice = nullptr;
        PresentFn g_originalPresent = nullptr;

        alignas(64) std::array<SharedFrameBatch, 2> g_batchSlots{};
        std::atomic<std::uint32_t> g_batchSequence{0};
        std::atomic<bool> g_batchAvailable{false};

        std::atomic<std::uint64_t> g_realPresentCount{0};
        std::atomic<std::uint64_t> g_generatedPresentCount{0};
        std::atomic<std::uint64_t> g_skippedPresentCount{0};
        std::atomic<std::uint64_t> g_frameLatencyTimeoutCount{0};
        std::atomic<std::uint64_t> g_presentFailureCount{0};
        std::atomic<std::uint64_t> g_stalePredictionCount{0};
        std::atomic<std::uint64_t> g_ringBusyDropCount{0};
        std::atomic<std::uint64_t> g_compositionFailureCount{0};

        std::thread g_presenterThread;
        std::mutex g_presenterMutex;
        std::condition_variable g_presenterCv;
        std::atomic<bool> g_presenterStop{false};
        std::atomic<double> g_realIntervalSeconds{1.0 / 30.0};
        std::atomic<double> g_outputIntervalSeconds{0.0};
        Clock::time_point g_lastRealPresent{};
        Clock::time_point g_lastOutputPresent{};

        ComPtr<ID3D11Device> g_outputDevice;
        ComPtr<ID3D11DeviceContext> g_outputContext;
        ComPtr<IDXGISwapChain1> g_outputSwapChain;
        ComPtr<IDXGISwapChain3> g_outputSwapChain3;
        ComPtr<IDCompositionDevice> g_dcompDevice;
        ComPtr<IDCompositionTarget> g_dcompTarget;
        ComPtr<IDCompositionVisual> g_dcompVisual;
        HANDLE g_pacingTimer = nullptr;
        HWND g_compositionOwner = nullptr;
        UINT g_outputWidth = 0;
        UINT g_outputHeight = 0;
        DXGI_FORMAT g_outputFormat = DXGI_FORMAT_UNKNOWN;
        std::atomic<bool> g_presenterReady{false};
        bool g_compositionVisible = false;
        std::atomic<int> g_monitorRefreshHz{60};
        std::array<OpenedSharedFrame, kOpenedCacheSize> g_openedCache{};

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

        SharedFrameBatch ReadLatestBatch()
        {
            if (!g_batchAvailable.load(std::memory_order_acquire)) return SharedFrameBatch{};
            SharedFrameBatch result{};
            for (int attempt = 0; attempt < 3; ++attempt)
            {
                const std::uint32_t before = g_batchSequence.load(std::memory_order_acquire);
                result = g_batchSlots[before & 1u];
                const std::uint32_t after = g_batchSequence.load(std::memory_order_acquire);
                if (before == after) return result;
            }
            const std::uint32_t seq = g_batchSequence.load(std::memory_order_acquire);
            return g_batchSlots[seq & 1u];
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

        void ClearOpenedCache()
        {
            for (OpenedSharedFrame& entry : g_openedCache)
            {
                entry.keyedMutex.Reset();
                entry.texture.Reset();
                entry.handle = nullptr;
            }
        }

        void ReleaseCompositionResources()
        {
            g_presenterReady.store(false, std::memory_order_release);
            g_outputSwapChainRaw.store(nullptr, std::memory_order_release);
            if (g_dcompVisual) g_dcompVisual->SetContent(nullptr);
            if (g_dcompTarget) g_dcompTarget->SetRoot(nullptr);
            if (g_dcompDevice) g_dcompDevice->Commit();
            ClearOpenedCache();
            g_outputSwapChain3.Reset();
            g_outputSwapChain.Reset();
            g_dcompVisual.Reset();
            g_dcompTarget.Reset();
            g_dcompDevice.Reset();
            g_outputContext.Reset();
            g_outputDevice.Reset();
            g_compositionOwner = nullptr;
            g_outputWidth = g_outputHeight = 0;
            g_outputFormat = DXGI_FORMAT_UNKNOWN;
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

        bool CreateIndependentDevice(IDXGIAdapter* adapter)
        {
            if (!adapter) return false;
            D3D_FEATURE_LEVEL created = D3D_FEATURE_LEVEL_11_0;
            const D3D_FEATURE_LEVEL requested[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
            HRESULT hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested, 2, D3D11_SDK_VERSION,
                &g_outputDevice, &created, &g_outputContext);
            if (hr == E_INVALIDARG)
            {
                const D3D_FEATURE_LEVEL fallback[] = {D3D_FEATURE_LEVEL_11_0};
                hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT, fallback, 1, D3D11_SDK_VERSION,
                    &g_outputDevice, &created, &g_outputContext);
            }
            return SUCCEEDED(hr) && g_outputDevice && g_outputContext;
        }

        bool CreateCompositionOutput(const SharedFrameBatch& batch)
        {
            if (!batch.sourceWindow || !IsWindow(batch.sourceWindow) || batch.width <= 0 || batch.height <= 0) return false;
            const DXGI_FORMAT wanted = OutputCompatibleFormat(batch.format);
            if (wanted == DXGI_FORMAT_UNKNOWN) return false;
            ReleaseCompositionResources();

            IDXGISwapChain* unityChain = g_unitySwapChain.load(std::memory_order_acquire);
            if (!unityChain) return false;
            ComPtr<ID3D11Device> unityDevice;
            ComPtr<IDXGIDevice> unityDxgi;
            ComPtr<IDXGIAdapter> adapter;
            ComPtr<IDXGIFactory2> factory;
            if (FAILED(unityChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(unityDevice.GetAddressOf()))) || !unityDevice ||
                FAILED(unityDevice.As(&unityDxgi)) || !unityDxgi ||
                FAILED(unityDxgi->GetAdapter(&adapter)) || !adapter ||
                FAILED(adapter->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(factory.GetAddressOf()))) || !factory ||
                !CreateIndependentDevice(adapter.Get()))
            {
                ReleaseCompositionResources();
                return false;
            }

            ComPtr<IDXGIDevice> outputDxgi;
            if (FAILED(g_outputDevice.As(&outputDxgi)) || !outputDxgi ||
                FAILED(DCompositionCreateDevice(outputDxgi.Get(), __uuidof(IDCompositionDevice), reinterpret_cast<void**>(g_dcompDevice.GetAddressOf()))) || !g_dcompDevice ||
                FAILED(g_dcompDevice->CreateTargetForHwnd(batch.sourceWindow, TRUE, &g_dcompTarget)) || !g_dcompTarget ||
                FAILED(g_dcompDevice->CreateVisual(&g_dcompVisual)) || !g_dcompVisual)
            {
                g_compositionFailureCount.fetch_add(1, std::memory_order_relaxed);
                ReleaseCompositionResources();
                return false;
            }

            DXGI_SWAP_CHAIN_DESC1 desc{};
            desc.Width = static_cast<UINT>(batch.width);
            desc.Height = static_cast<UINT>(batch.height);
            desc.Format = wanted;
            desc.Stereo = FALSE;
            desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 3;
            desc.Scaling = DXGI_SCALING_STRETCH;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
            desc.Flags = 0;

            if (FAILED(factory->CreateSwapChainForComposition(g_outputDevice.Get(), &desc, nullptr, &g_outputSwapChain)) || !g_outputSwapChain)
            {
                desc.BufferCount = 2;
                desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
                if (FAILED(factory->CreateSwapChainForComposition(g_outputDevice.Get(), &desc, nullptr, &g_outputSwapChain)) || !g_outputSwapChain)
                {
                    g_compositionFailureCount.fetch_add(1, std::memory_order_relaxed);
                    ReleaseCompositionResources();
                    return false;
                }
            }

            g_outputSwapChain.As(&g_outputSwapChain3);
            if (FAILED(g_dcompVisual->SetContent(nullptr)) ||
                FAILED(g_dcompTarget->SetRoot(g_dcompVisual.Get())) ||
                FAILED(g_dcompDevice->Commit()))
            {
                g_compositionFailureCount.fetch_add(1, std::memory_order_relaxed);
                ReleaseCompositionResources();
                return false;
            }

            g_outputWidth = static_cast<UINT>(batch.width);
            g_outputHeight = static_cast<UINT>(batch.height);
            g_outputFormat = wanted;
            g_compositionOwner = batch.sourceWindow;
            g_compositionVisible = false;
            g_monitorRefreshHz.store(QueryMonitorRefreshHz(batch.sourceWindow), std::memory_order_release);
            g_outputSwapChainRaw.store(g_outputSwapChain.Get(), std::memory_order_release);
            g_presenterReady.store(true, std::memory_order_release);
            return true;
        }

        bool EnsureCompositionOutput(const SharedFrameBatch& batch)
        {
            const DXGI_FORMAT wanted = OutputCompatibleFormat(batch.format);
            if (wanted == DXGI_FORMAT_UNKNOWN) return false;
            if (g_presenterReady.load(std::memory_order_acquire) && g_outputSwapChain && g_outputDevice &&
                g_compositionOwner == batch.sourceWindow &&
                g_outputWidth == static_cast<UINT>(batch.width) &&
                g_outputHeight == static_cast<UINT>(batch.height) && g_outputFormat == wanted)
                return true;
            return CreateCompositionOutput(batch);
        }

        OpenedSharedFrame* GetOpenedShared(HANDLE handle)
        {
            if (!handle || !g_outputDevice) return nullptr;
            for (OpenedSharedFrame& entry : g_openedCache)
                if (entry.handle == handle && entry.texture && entry.keyedMutex) return &entry;

            OpenedSharedFrame* target = nullptr;
            for (OpenedSharedFrame& entry : g_openedCache)
            {
                if (!entry.handle)
                {
                    target = &entry;
                    break;
                }
            }
            if (!target)
            {
                ClearOpenedCache();
                target = &g_openedCache[0];
            }

            ComPtr<ID3D11Texture2D> texture;
            if (FAILED(g_outputDevice->OpenSharedResource(handle, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(texture.GetAddressOf()))) || !texture)
                return nullptr;
            ComPtr<IDXGIKeyedMutex> keyed;
            if (FAILED(texture.As(&keyed)) || !keyed) return nullptr;
            target->handle = handle;
            target->texture = texture;
            target->keyedMutex = keyed;
            return target;
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
            LARGE_INTEGER due{};
            due.QuadPart = -std::max<LONGLONG>(1, static_cast<LONGLONG>(ns / 100));
            if (!SetWaitableTimerEx(g_pacingTimer, &due, 0, nullptr, nullptr, nullptr, 0))
            {
                std::this_thread::sleep_until(deadline);
                return;
            }
            WaitForSingleObject(g_pacingTimer, 1000);
        }

        bool PresentSharedHandle(const SharedFrameBatch& batch, HANDLE handle)
        {
            if (!handle || !g_outputDevice || !g_outputContext || !g_outputSwapChain) return false;
            OpenedSharedFrame* shared = GetOpenedShared(handle);
            if (!shared || !shared->texture || !shared->keyedMutex) return false;

            const HRESULT acquire = shared->keyedMutex->AcquireSync(1, 0);
            if (acquire != S_OK)
            {
                g_frameLatencyTimeoutCount.fetch_add(1, std::memory_order_relaxed);
                return false;
            }

            bool copied = false;
            ComPtr<ID3D11Texture2D> output;
            if (GetCurrentOutputBackBuffer(output) && output)
            {
                D3D11_TEXTURE2D_DESC src{}, dst{};
                shared->texture->GetDesc(&src);
                output->GetDesc(&dst);
                if (src.Width == dst.Width && src.Height == dst.Height && src.SampleDesc.Count == 1 && dst.SampleDesc.Count == 1)
                {
                    g_outputContext->CopyResource(output.Get(), shared->texture.Get());
                    copied = true;
                }
            }
            shared->keyedMutex->ReleaseSync(0);
            if (!copied) return false;

            const HRESULT hr = g_outputSwapChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
            {
                g_frameLatencyTimeoutCount.fetch_add(1, std::memory_order_relaxed);
                return false;
            }
            if (FAILED(hr))
            {
                g_presentFailureCount.fetch_add(1, std::memory_order_relaxed);
                if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET)
                    ReleaseCompositionResources();
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

        void PresenterMain()
        {
            std::uint64_t activeBatchId = 0;
            int cursor = 0;
            Clock::time_point nextOutput{};
            Clock::time_point nextRefreshQuery{};
            int lastEffectiveFps = 0;

            while (!g_presenterStop.load(std::memory_order_acquire))
            {
                const PresentMode mode = static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire));
                SharedFrameBatch latest = ReadLatestBatch();
                if (mode == PresentMode::Disabled || latest.batchId == 0 || latest.frameCount <= 0)
                {
                    if (g_dcompVisual) SetCompositionVisible(false);
                    activeBatchId = 0;
                    cursor = 0;
                    nextOutput = Clock::time_point{};
                    std::unique_lock<std::mutex> lock(g_presenterMutex);
                    g_presenterCv.wait_for(lock, std::chrono::milliseconds(20));
                    continue;
                }
                if (!IsSourceWindowUsable(latest.sourceWindow))
                {
                    if (g_dcompVisual) SetCompositionVisible(false);
                    activeBatchId = 0;
                    cursor = 0;
                    nextOutput = Clock::time_point{};
                    std::this_thread::sleep_for(std::chrono::milliseconds(25));
                    continue;
                }

                const double baseFps = 1.0 / std::max(1.0 / 1000.0, g_realIntervalSeconds.load(std::memory_order_relaxed));
                const int targetFps = std::max(1, g_targetOutputFps.load(std::memory_order_acquire));
                if (static_cast<double>(targetFps) <= baseFps + 0.5)
                {
                    if (g_dcompVisual) SetCompositionVisible(false);
                    activeBatchId = 0;
                    cursor = 0;
                    nextOutput = Clock::time_point{};
                    std::this_thread::sleep_for(std::chrono::milliseconds(4));
                    continue;
                }

                if (!EnsureCompositionOutput(latest))
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
                    g_monitorRefreshHz.store(QueryMonitorRefreshHz(latest.sourceWindow), std::memory_order_release);
                    nextRefreshQuery = now + std::chrono::seconds(1);
                }
                const int monitorHz = std::max(24, g_monitorRefreshHz.load(std::memory_order_acquire));
                const int effectiveFps = std::max(1, std::min(targetFps, monitorHz));
                const auto period = std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(1.0 / static_cast<double>(effectiveFps)));
                if (nextOutput.time_since_epoch().count() == 0 || effectiveFps != lastEffectiveFps || now > nextOutput + period * 3)
                    nextOutput = now;
                lastEffectiveFps = effectiveFps;

                if (latest.batchId != activeBatchId)
                {
                    if (activeBatchId != 0 && cursor < latest.frameCount)
                        g_stalePredictionCount.fetch_add(static_cast<std::uint64_t>(std::max(0, latest.frameCount - cursor)), std::memory_order_relaxed);
                    activeBatchId = latest.batchId;
                    cursor = 0;
                }

                WaitUntilDeadline(nextOutput);
                nextOutput += period;
                if (g_presenterStop.load(std::memory_order_acquire)) break;

                latest = ReadLatestBatch();
                if (latest.batchId != activeBatchId)
                {
                    activeBatchId = latest.batchId;
                    cursor = 0;
                }
                if (cursor >= latest.frameCount)
                {
                    const Clock::time_point afterIdle = Clock::now();
                    if (afterIdle > nextOutput + period) nextOutput = afterIdle + period;
                    continue;
                }

                const int frameIndex = cursor;
                const HANDLE handle = latest.handles[frameIndex];
                const bool presented = PresentSharedHandle(latest, handle);
                ++cursor;
                if (presented)
                {
                    if (frameIndex == latest.realFrameIndex)
                        g_realPresentCount.fetch_add(1, std::memory_order_relaxed);
                    else
                        g_generatedPresentCount.fetch_add(1, std::memory_order_relaxed);
                    SetCompositionVisible(true);
                }
                else
                {
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
                }

                const Clock::time_point after = Clock::now();
                if (after > nextOutput + period) nextOutput = after + period;
            }

            if (g_dcompVisual) SetCompositionVisible(false);
            ReleaseCompositionResources();
            if (g_pacingTimer)
            {
                CloseHandle(g_pacingTimer);
                g_pacingTimer = nullptr;
            }
        }

        void UpdateRealTiming(Clock::time_point now)
        {
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
        }

        HRESULT __stdcall HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
        {
            const bool target = IsTargetSwapChain(swapChain);
            bool producerAdvanced = false;
            PresentMode mode = PresentMode::Disabled;
            if (target)
            {
                g_unitySwapChain.store(swapChain, std::memory_order_release);
                mode = static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire));
                if (mode != PresentMode::Disabled && (flags & DXGI_PRESENT_TEST) == 0)
                {
                    BackbufferGenerationCallback capture = g_captureCallback.load(std::memory_order_acquire);
                    ComPtr<ID3D11Texture2D> backBuffer;
                    DXGI_SWAP_CHAIN_DESC desc{};
                    if (capture && GetCurrentBackBuffer(swapChain, backBuffer) && backBuffer && SUCCEEDED(swapChain->GetDesc(&desc)))
                        producerAdvanced = capture(backBuffer.Get(), desc.OutputWindow);
                }
            }

            const HRESULT hr = g_originalPresent ? g_originalPresent(swapChain, syncInterval, flags) : E_FAIL;
            if (target && (flags & DXGI_PRESENT_TEST) == 0 && SUCCEEDED(hr))
            {
                UpdateRealTiming(Clock::now());
                if (mode != PresentMode::Disabled && !producerAdvanced)
                    g_ringBusyDropCount.fetch_add(1, std::memory_order_relaxed);
            }
            return hr;
        }

        bool ResolvePresentAddress(void** outAddress)
        {
            if (!outAddress) return false;
            *outAddress = nullptr;
            const wchar_t* className = L"RimFG_DummyDX11Window";
            WNDCLASSEXW wc{};
            wc.cbSize = sizeof(wc);
            wc.lpfnWndProc = DummyWndProc;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = className;
            const ATOM atom = RegisterClassExW(&wc);
            if (!atom && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
            HWND hwnd = CreateWindowExW(0, className, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
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
            D3D_FEATURE_LEVEL requested[] = {D3D_FEATURE_LEVEL_11_0}, created{};
            ComPtr<ID3D11Device> device;
            ComPtr<ID3D11DeviceContext> context;
            ComPtr<IDXGISwapChain> chain;
            const HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
                requested, 1, D3D11_SDK_VERSION, &desc, &chain, &device, &created, &context);
            if (SUCCEEDED(hr) && chain)
            {
                void** vtable = *reinterpret_cast<void***>(chain.Get());
                *outAddress = vtable[8];
            }
            DestroyWindow(hwnd);
            UnregisterClassW(className, wc.hInstance);
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
        if (MH_EnableHook(presentAddress) != MH_OK)
        {
            MH_RemoveHook(presentAddress);
            g_originalPresent = nullptr;
            return false;
        }

        g_presenterStop.store(false, std::memory_order_release);
        g_lastRealPresent = Clock::time_point{};
        g_lastOutputPresent = Clock::time_point{};
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
        g_captureCallback.store(nullptr, std::memory_order_release);
        if (g_installed.exchange(false, std::memory_order_acq_rel))
        {
            MH_DisableHook(MH_ALL_HOOKS);
            MH_Uninitialize();
        }
        g_originalPresent = nullptr;
        g_unitySwapChain.store(nullptr, std::memory_order_release);
        g_unityDevice = nullptr;
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

    PresentMode GetPresentMode()
    {
        return static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire));
    }

    void SetBackbufferGenerationCallback(BackbufferGenerationCallback callback)
    {
        g_captureCallback.store(callback, std::memory_order_release);
    }

    void PublishSharedFrameBatch(const SharedFrameBatch& batch)
    {
        if (batch.batchId == 0 || batch.frameCount <= 0 || batch.frameCount > MaxBatchFrames ||
            batch.width <= 0 || batch.height <= 0 || !batch.sourceWindow)
            return;
        const std::uint32_t next = g_batchSequence.load(std::memory_order_relaxed) + 1u;
        g_batchSlots[next & 1u] = batch;
        g_batchSequence.store(next, std::memory_order_release);
        g_batchAvailable.store(true, std::memory_order_release);
        g_presenterCv.notify_all();
    }

    void ClearGeneratedFrameSource()
    {
        g_batchAvailable.store(false, std::memory_order_release);
        const std::uint32_t next = g_batchSequence.load(std::memory_order_relaxed) + 1u;
        g_batchSlots[next & 1u] = SharedFrameBatch{};
        g_batchSequence.store(next, std::memory_order_release);
        g_presenterCv.notify_all();
    }

    void SetTargetOutputFps(int fps)
    {
        g_targetOutputFps.store(std::max(1, std::min(1000000000, fps)), std::memory_order_release);
        g_presenterCv.notify_all();
    }
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
