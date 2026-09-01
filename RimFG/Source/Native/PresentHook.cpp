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
#include <dxgi.h>
#include <dxgi1_2.h>
#include <dxgi1_3.h>
#include <dxgi1_4.h>
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
        constexpr wchar_t kOutputWindowClass[] = L"RimFG_OutputWindow";

        struct GeneratedSourceSlot
        {
            ID3D11Texture2D* texture = nullptr;
            int width = 0;
            int height = 0;
            int hudCount = 0;
            std::uint32_t frameIndex = 0;
            std::array<HudRectPx, kMaxHudRects> hud{};
        };

        struct RealFrameSlot
        {
            ComPtr<ID3D11Texture2D> snapshot;
            ComPtr<ID3D11Texture2D> composite;
            UINT width = 0;
            UINT height = 0;
            DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
            std::uint64_t sequence = 0;
            Clock::time_point capturedAt{};
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
        std::atomic<std::uint64_t> g_generatedPresentCount{0};
        std::atomic<std::uint64_t> g_skippedPresentCount{0};

        std::array<RealFrameSlot, 3> g_realSlots{};
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
        std::uint64_t g_realSequence = 0;
        Clock::time_point g_lastRealPresent{};
        Clock::time_point g_latestRealTime{};
        double g_realIntervalSeconds = 1.0 / 30.0;
        double g_outputIntervalSeconds = 0.0;
        Clock::time_point g_lastOutputPresent{};

        HWND g_outputWindow = nullptr;
        HWND g_outputOwner = nullptr;
        ComPtr<IDXGISwapChain1> g_outputSwapChain;
        ComPtr<IDXGISwapChain2> g_outputSwapChain2;
        HANDLE g_frameLatencyWaitable = nullptr;
        UINT g_outputWidth = 0;
        UINT g_outputHeight = 0;
        DXGI_FORMAT g_outputFormat = DXGI_FORMAT_UNKNOWN;
        bool g_outputVisible = false;
        int g_monitorRefreshHz = 60;

        LRESULT CALLBACK DummyWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
        {
            return DefWindowProcW(hwnd, msg, wp, lp);
        }

        LRESULT CALLBACK OutputWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
        {
            switch (msg)
            {
            case WM_NCHITTEST:
                return HTTRANSPARENT;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_ERASEBKGND:
                return 1;
            case WM_CLOSE:
                ShowWindow(hwnd, SW_HIDE);
                return 0;
            default:
                return DefWindowProcW(hwnd, msg, wp, lp);
            }
        }

        void PumpOutputMessages()
        {
            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
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
            if (swapChain == g_outputSwapChainRaw.load(std::memory_order_acquire)) return false;

            DXGI_SWAP_CHAIN_DESC desc{};
            if (FAILED(swapChain->GetDesc(&desc)) || !IsCurrentProcessWindow(desc.OutputWindow))
                return false;

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

        UINT CurrentBackBufferIndex(IDXGISwapChain* swapChain)
        {
            if (!swapChain) return 0;
            ComPtr<IDXGISwapChain3> chain3;
            if (SUCCEEDED(swapChain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(chain3.GetAddressOf()))) && chain3)
                return chain3->GetCurrentBackBufferIndex();
            return 0;
        }

        bool GetCurrentBackBuffer(IDXGISwapChain* swapChain, ComPtr<ID3D11Texture2D>& out)
        {
            out.Reset();
            if (!swapChain) return false;
            const UINT index = CurrentBackBufferIndex(swapChain);
            if (SUCCEEDED(swapChain->GetBuffer(index, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(out.GetAddressOf()))) && out)
                return true;
            return index != 0 && SUCCEEDED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(out.GetAddressOf()))) && out;
        }

        void CopyHudRects(ID3D11DeviceContext* context, ID3D11Texture2D* realFrame, ID3D11Texture2D* composite, const GeneratedSourceSlot& source, UINT width, UINT height)
        {
            if (!context || !realFrame || !composite) return;
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

        void ReleaseOutputSwapChain()
        {
            g_outputSwapChainRaw.store(nullptr, std::memory_order_release);
            g_frameLatencyWaitable = nullptr;
            g_outputSwapChain2.Reset();
            g_outputSwapChain.Reset();
            g_outputWidth = 0;
            g_outputHeight = 0;
            g_outputFormat = DXGI_FORMAT_UNKNOWN;
        }

        void DestroyOutputWindow()
        {
            ReleaseOutputSwapChain();
            if (g_outputWindow)
            {
                DestroyWindow(g_outputWindow);
                g_outputWindow = nullptr;
            }
            g_outputOwner = nullptr;
            g_outputVisible = false;
        }

        void ReleaseAsyncResources()
        {
            for (auto& slot : g_realSlots)
            {
                slot.snapshot.Reset();
                slot.composite.Reset();
                slot.width = 0;
                slot.height = 0;
                slot.format = DXGI_FORMAT_UNKNOWN;
                slot.sequence = 0;
                slot.capturedAt = Clock::time_point{};
                slot.sourceWindow = nullptr;
                slot.source = GeneratedSourceSlot{};
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
                g_realSlots[0].snapshot && g_realSlots[0].composite &&
                g_realSlots[1].snapshot && g_realSlots[1].composite &&
                g_realSlots[2].snapshot && g_realSlots[2].composite)
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

            for (auto& slot : g_realSlots)
            {
                if (FAILED(device->CreateTexture2D(&copy, nullptr, &slot.snapshot)) ||
                    FAILED(device->CreateTexture2D(&copy, nullptr, &slot.composite)))
                {
                    ReleaseAsyncResources();
                    return false;
                }
            }

            g_asyncWidth = desc.Width;
            g_asyncHeight = desc.Height;
            g_asyncFormat = desc.Format;
            return true;
        }

        DXGI_FORMAT OutputCompatibleFormat(DXGI_FORMAT format)
        {
            switch (format)
            {
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
            default: return format;
            }
        }

        int QueryMonitorRefreshHz(HWND owner)
        {
            if (!owner) return 60;
            const HMONITOR monitor = MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST);
            MONITORINFOEXW mi{};
            mi.cbSize = sizeof(mi);
            if (!GetMonitorInfoW(monitor, &mi)) return 60;
            DEVMODEW dm{};
            dm.dmSize = sizeof(dm);
            if (!EnumDisplaySettingsW(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm)) return 60;
            const int hz = static_cast<int>(dm.dmDisplayFrequency);
            return hz >= 24 && hz <= 1000 ? hz : 60;
        }

        bool RegisterOutputWindowClass()
        {
            WNDCLASSEXW wc{};
            wc.cbSize = sizeof(wc);
            wc.lpfnWndProc = OutputWndProc;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = kOutputWindowClass;
            wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
            const ATOM atom = RegisterClassExW(&wc);
            return atom != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
        }

        bool AlignOutputWindow(HWND owner, UINT width, UINT height)
        {
            if (!g_outputWindow || !owner || !IsWindow(owner)) return false;
            POINT topLeft{0, 0};
            if (!ClientToScreen(owner, &topLeft)) return false;
            const int w = static_cast<int>(width);
            const int h = static_cast<int>(height);
            return SetWindowPos(g_outputWindow, HWND_TOP, topLeft.x, topLeft.y, w, h,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING) != FALSE;
        }

        bool EnsureOutputWindow(HWND owner, UINT width, UINT height)
        {
            if (!owner || !width || !height) return false;
            if (g_outputWindow && g_outputOwner != owner)
                DestroyOutputWindow();

            if (!g_outputWindow)
            {
                if (!RegisterOutputWindowClass()) return false;
                const DWORD exStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
                g_outputWindow = CreateWindowExW(exStyle, kOutputWindowClass, L"RimFG Output", WS_POPUP,
                    0, 0, static_cast<int>(width), static_cast<int>(height), owner, nullptr, GetModuleHandleW(nullptr), nullptr);
                if (!g_outputWindow) return false;
                g_outputOwner = owner;
            }
            return AlignOutputWindow(owner, width, height);
        }

        bool EnsureOutputSwapChain(const RealFrameSlot& slot)
        {
            if (!g_asyncDevice || !g_asyncContext || !slot.sourceWindow || !slot.width || !slot.height)
                return false;
            if (!EnsureOutputWindow(slot.sourceWindow, slot.width, slot.height))
                return false;

            const DXGI_FORMAT wantedFormat = OutputCompatibleFormat(slot.format);
            if (g_outputSwapChain && g_outputWidth == slot.width && g_outputHeight == slot.height && g_outputFormat == wantedFormat)
                return true;

            ReleaseOutputSwapChain();

            ComPtr<IDXGIDevice> dxgiDevice;
            ComPtr<IDXGIAdapter> adapter;
            ComPtr<IDXGIFactory2> factory;
            if (FAILED(g_asyncDevice.As(&dxgiDevice)) || !dxgiDevice ||
                FAILED(dxgiDevice->GetAdapter(&adapter)) || !adapter)
                return false;
            ComPtr<IDXGIFactory> baseFactory;
            if (FAILED(adapter->GetParent(__uuidof(IDXGIFactory), reinterpret_cast<void**>(baseFactory.GetAddressOf()))) || !baseFactory ||
                FAILED(baseFactory.As(&factory)) || !factory)
                return false;

            DXGI_SWAP_CHAIN_DESC1 desc{};
            desc.Width = slot.width;
            desc.Height = slot.height;
            desc.Format = wantedFormat;
            desc.Stereo = FALSE;
            desc.SampleDesc.Count = 1;
            desc.SampleDesc.Quality = 0;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 2;
            desc.Scaling = DXGI_SCALING_STRETCH;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
            desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

            if (FAILED(factory->CreateSwapChainForHwnd(g_asyncDevice.Get(), g_outputWindow, &desc, nullptr, nullptr, &g_outputSwapChain)) || !g_outputSwapChain)
                return false;
            factory->MakeWindowAssociation(g_outputWindow, DXGI_MWA_NO_ALT_ENTER);
            g_outputSwapChain.As(&g_outputSwapChain2);
            if (g_outputSwapChain2)
            {
                g_outputSwapChain2->SetMaximumFrameLatency(1);
                g_frameLatencyWaitable = g_outputSwapChain2->GetFrameLatencyWaitableObject();
            }

            g_outputWidth = slot.width;
            g_outputHeight = slot.height;
            g_outputFormat = wantedFormat;
            g_outputSwapChainRaw.store(g_outputSwapChain.Get(), std::memory_order_release);
            g_monitorRefreshHz = QueryMonitorRefreshHz(slot.sourceWindow);
            return true;
        }

        bool CopyToOutputBackBuffer(ID3D11Texture2D* source)
        {
            if (!source || !g_outputSwapChain || !g_asyncContext) return false;
            ComPtr<ID3D11Texture2D> outputBackBuffer;
            if (FAILED(g_outputSwapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(outputBackBuffer.GetAddressOf()))) || !outputBackBuffer)
                return false;

            D3D11_TEXTURE2D_DESC srcDesc{}, dstDesc{};
            source->GetDesc(&srcDesc);
            outputBackBuffer->GetDesc(&dstDesc);
            if (srcDesc.Width != dstDesc.Width || srcDesc.Height != dstDesc.Height || srcDesc.SampleDesc.Count != 1 || dstDesc.SampleDesc.Count != 1)
                return false;

            // UNORM/SRGB pairs are in the same DXGI format family and support GPU copy.
            g_asyncContext->CopyResource(outputBackBuffer.Get(), source);
            return true;
        }

        bool PresentOutputTexture(ID3D11Texture2D* source)
        {
            if (!source || !g_outputSwapChain) return false;
            if (g_frameLatencyWaitable)
            {
                const DWORD wait = WaitForSingleObject(g_frameLatencyWaitable, 20);
                if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED)
                    return false;
            }
            if (!CopyToOutputBackBuffer(source)) return false;
            const HRESULT hr = g_outputSwapChain->Present(0, 0);
            if (FAILED(hr)) return false;

            const Clock::time_point now = Clock::now();
            if (g_lastOutputPresent.time_since_epoch().count() != 0)
            {
                const double dt = std::chrono::duration<double>(now - g_lastOutputPresent).count();
                if (dt > 0.0005 && dt < 0.5)
                {
                    if (g_outputIntervalSeconds <= 0.0) g_outputIntervalSeconds = dt;
                    else g_outputIntervalSeconds += (dt - g_outputIntervalSeconds) * 0.12;
                }
            }
            g_lastOutputPresent = now;
            return true;
        }

        bool PresentReal(const RealFrameSlot& slot)
        {
            return slot.snapshot && PresentOutputTexture(slot.snapshot.Get());
        }

        bool PresentPrediction(const RealFrameSlot& slot, float fraction)
        {
            if (!slot.snapshot || !slot.composite || !g_asyncContext) return false;
            PredictionGenerationCallback predictor = g_predictionCallback.load(std::memory_order_acquire);
            if (!predictor) return false;
            ID3D11Texture2D* predicted = predictor(fraction);
            if (!predicted) return false;

            D3D11_TEXTURE2D_DESC predDesc{};
            predicted->GetDesc(&predDesc);
            if (predDesc.Width != slot.width || predDesc.Height != slot.height || predDesc.SampleDesc.Count != 1)
                return false;

            g_asyncContext->CopyResource(slot.composite.Get(), predicted);
            CopyHudRects(g_asyncContext.Get(), slot.snapshot.Get(), slot.composite.Get(), slot.source, slot.width, slot.height);
            return PresentOutputTexture(slot.composite.Get());
        }

        std::uint64_t CaptureRealFrameSlot(IDXGISwapChain* swapChain, Clock::time_point now)
        {
            if (!swapChain) return 0;
            ComPtr<ID3D11Device> device;
            if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(device.GetAddressOf()))) || !device) return 0;
            ComPtr<ID3D11Texture2D> backBuffer;
            if (!GetCurrentBackBuffer(swapChain, backBuffer) || !backBuffer) return 0;

            D3D11_TEXTURE2D_DESC backDesc{};
            backBuffer->GetDesc(&backDesc);
            if (!backDesc.Width || !backDesc.Height || backDesc.SampleDesc.Count != 1) return 0;

            DXGI_SWAP_CHAIN_DESC chainDesc{};
            if (FAILED(swapChain->GetDesc(&chainDesc)) || !chainDesc.OutputWindow) return 0;

            std::lock_guard<std::mutex> lock(g_presenterMutex);
            if (!EnsureAsyncResources(device.Get(), backBuffer.Get())) return 0;

            const std::uint64_t next = g_realSequence + 1u;
            RealFrameSlot& slot = g_realSlots[next % g_realSlots.size()];
            g_asyncContext->CopyResource(slot.snapshot.Get(), backBuffer.Get());
            slot.width = backDesc.Width;
            slot.height = backDesc.Height;
            slot.format = backDesc.Format;
            slot.sequence = next;
            slot.capturedAt = now;
            slot.sourceWindow = chainDesc.OutputWindow;
            slot.source = ReadLatestSource();
            return next;
        }

        void PublishRealFrame(std::uint64_t sequence, Clock::time_point now)
        {
            if (!sequence) return;
            {
                std::lock_guard<std::mutex> lock(g_presenterMutex);
                if (g_lastRealPresent.time_since_epoch().count() != 0)
                {
                    const double seconds = std::chrono::duration<double>(now - g_lastRealPresent).count();
                    if (seconds >= 1.0 / 1000.0 && seconds <= 0.5)
                        g_realIntervalSeconds += (seconds - g_realIntervalSeconds) * 0.20;
                }
                g_lastRealPresent = now;
                g_latestRealTime = now;
                g_realSequence = sequence;
            }
            g_presenterCv.notify_all();
        }

        bool CopyLatestRealSlot(RealFrameSlot& out, Clock::time_point& latestRealTime)
        {
            std::lock_guard<std::mutex> lock(g_presenterMutex);
            if (!g_realSequence) return false;
            const RealFrameSlot& slot = g_realSlots[g_realSequence % g_realSlots.size()];
            if (slot.sequence != g_realSequence || !slot.snapshot) return false;
            out = slot;
            latestRealTime = g_latestRealTime;
            return true;
        }

        void SetOutputVisible(bool visible)
        {
            if (!g_outputWindow) return;
            if (visible == g_outputVisible) return;
            ShowWindow(g_outputWindow, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
            g_outputVisible = visible;
        }

        void PresenterMain()
        {
            std::uint64_t lastDisplayedRealSequence = 0;
            Clock::time_point nextOutput{};

            for (;;)
            {
                PumpOutputMessages();
                {
                    std::lock_guard<std::mutex> lock(g_presenterMutex);
                    if (g_presenterStop) break;
                }

                const PresentMode mode = static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire));
                RealFrameSlot slot;
                Clock::time_point latestRealTime{};
                if (mode == PresentMode::Disabled || !CopyLatestRealSlot(slot, latestRealTime))
                {
                    SetOutputVisible(false);
                    std::unique_lock<std::mutex> lock(g_presenterMutex);
                    g_presenterCv.wait_for(lock, std::chrono::milliseconds(20), [&] { return g_presenterStop || g_realSequence != 0; });
                    continue;
                }

                const double baseFps = 1.0 / std::max(1.0 / 1000.0, g_realIntervalSeconds);
                const int targetFps = std::max(1, g_targetOutputFps.load(std::memory_order_acquire));
                if (static_cast<double>(targetFps) <= baseFps + 0.5)
                {
                    SetOutputVisible(false);
                    nextOutput = Clock::time_point{};
                    std::this_thread::sleep_for(std::chrono::milliseconds(4));
                    continue;
                }

                if (!EnsureOutputSwapChain(slot))
                {
                    SetOutputVisible(false);
                    g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
                    std::this_thread::sleep_for(std::chrono::milliseconds(10));
                    continue;
                }

                AlignOutputWindow(slot.sourceWindow, slot.width, slot.height);
                const int effectiveHz = std::max(1, std::min(targetFps, std::max(24, g_monitorRefreshHz)));
                const auto period = std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(1.0 / static_cast<double>(effectiveHz)));
                const Clock::time_point now = Clock::now();
                if (nextOutput.time_since_epoch().count() == 0 || now > nextOutput + period * 3)
                    nextOutput = now;
                if (now < nextOutput)
                {
                    std::this_thread::sleep_until(nextOutput);
                    continue;
                }

                // Re-sample immediately before the output slot so a newly arrived real
                // frame wins over prediction without changing the fixed output clock.
                CopyLatestRealSlot(slot, latestRealTime);

                bool presented = false;
                if (slot.sequence != lastDisplayedRealSequence)
                {
                    presented = PresentReal(slot);
                    if (presented)
                        lastDisplayedRealSequence = slot.sequence;
                }
                else
                {
                    const double age = std::max(0.0, std::chrono::duration<double>(Clock::now() - latestRealTime).count());
                    const double fractionD = age / std::max(1.0 / 1000.0, g_realIntervalSeconds);
                    const float fraction = static_cast<float>(std::max(0.02, std::min(0.999999, fractionD)));
                    presented = PresentPrediction(slot, fraction);
                    if (presented)
                        g_generatedPresentCount.fetch_add(1, std::memory_order_relaxed);
                    else
                        g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);
                }

                if (presented)
                    SetOutputVisible(true);

                nextOutput += period;
                if (Clock::now() > nextOutput + period)
                    nextOutput = Clock::now() + period;
            }

            SetOutputVisible(false);
            DestroyOutputWindow();
        }

        HRESULT __stdcall HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
        {
            const bool target = IsTargetSwapChain(swapChain);
            std::uint64_t capturedSequence = 0;
            PresentMode mode = PresentMode::Disabled;

            if (target)
            {
                g_unitySwapChain.store(swapChain, std::memory_order_release);
                mode = static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire));

                BackbufferGenerationCallback capture = g_captureCallback.load(std::memory_order_acquire);
                if (capture && (flags & DXGI_PRESENT_TEST) == 0)
                {
                    ComPtr<ID3D11Texture2D> backBuffer;
                    if (GetCurrentBackBuffer(swapChain, backBuffer) && backBuffer)
                        capture(backBuffer.Get());
                }

                if (mode != PresentMode::Disabled && (flags & DXGI_PRESENT_TEST) == 0)
                    capturedSequence = CaptureRealFrameSlot(swapChain, Clock::now());
            }

            // RimWorld/Unity owns this Present completely. RimFG never injects another
            // Present into the Unity swapchain anymore.
            const HRESULT hr = g_originalPresent ? g_originalPresent(swapChain, syncInterval, flags) : E_FAIL;

            if (target && mode != PresentMode::Disabled && (flags & DXGI_PRESENT_TEST) == 0 && SUCCEEDED(hr) && capturedSequence)
                PublishRealFrame(capturedSequence, Clock::now());
            else if (target && mode != PresentMode::Disabled && (flags & DXGI_PRESENT_TEST) == 0 && !capturedSequence)
                g_skippedPresentCount.fetch_add(1, std::memory_order_relaxed);

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

            D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_0 }, created{};
            ComPtr<ID3D11Device> device;
            ComPtr<ID3D11DeviceContext> context;
            ComPtr<IDXGISwapChain> chain;
            const HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, requested, 1, D3D11_SDK_VERSION, &desc, &chain, &device, &created, &context);
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

        {
            std::lock_guard<std::mutex> lock(g_presenterMutex);
            g_presenterStop = false;
            g_realSequence = 0;
            g_lastRealPresent = Clock::time_point{};
            g_latestRealTime = Clock::time_point{};
            g_outputIntervalSeconds = 0.0;
            g_lastOutputPresent = Clock::time_point{};
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
        }
        g_presenterCv.notify_all();
        if (g_presenterThread.joinable()) g_presenterThread.join();

        ClearGeneratedFrameSource();
        ReleaseAsyncResources();
        g_captureCallback.store(nullptr, std::memory_order_release);
        g_predictionCallback.store(nullptr, std::memory_order_release);

        if (g_installed.exchange(false, std::memory_order_acq_rel))
        {
            MH_DisableHook(MH_ALL_HOOKS);
            MH_Uninitialize();
        }

        g_originalPresent = nullptr;
        g_unitySwapChain.store(nullptr, std::memory_order_release);
        g_outputSwapChainRaw.store(nullptr, std::memory_order_release);
        g_unityDevice = nullptr;
    }

    bool IsInstalled() { return g_installed.load(std::memory_order_acquire); }
    bool HasUnitySwapChain() { return g_unitySwapChain.load(std::memory_order_acquire) != nullptr; }
    IDXGISwapChain* GetUnitySwapChain() { return g_unitySwapChain.load(std::memory_order_acquire); }

    void SetPresentMode(PresentMode mode)
    {
        g_presentMode.store(static_cast<int>(mode), std::memory_order_release);
        g_presenterCv.notify_all();
    }

    PresentMode GetPresentMode() { return static_cast<PresentMode>(g_presentMode.load(std::memory_order_acquire)); }
    void SetBackbufferGenerationCallback(BackbufferGenerationCallback callback) { g_captureCallback.store(callback, std::memory_order_release); }
    void SetPredictionGenerationCallback(PredictionGenerationCallback callback) { g_predictionCallback.store(callback, std::memory_order_release); }

    void SetGeneratedFrameSource(ID3D11Texture2D* generatedFrame, int width, int height, const HudRectPx* hudRects, int hudRectCount, std::uint32_t frameIndex)
    {
        if (!generatedFrame || width <= 0 || height <= 0)
        {
            ClearGeneratedFrameSource();
            return;
        }

        const std::uint32_t next = g_sourceSequence.load(std::memory_order_relaxed) + 1u;
        auto& slot = g_sourceSlots[next & 1u];
        slot.texture = generatedFrame;
        slot.width = width;
        slot.height = height;
        slot.frameIndex = frameIndex;
        slot.hudCount = std::max(0, std::min(hudRectCount, kMaxHudRects));
        if (hudRects)
            for (int i = 0; i < slot.hudCount; ++i)
                slot.hud[static_cast<std::size_t>(i)] = hudRects[i];

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

    void SetTargetOutputFps(int fps)
    {
        g_targetOutputFps.store(std::max(1, std::min(1000000000, fps)), std::memory_order_release);
        g_presenterCv.notify_all();
    }

    int GetTargetOutputFps() { return g_targetOutputFps.load(std::memory_order_acquire); }
    double EstimatedBaseFps() { return 1.0 / std::max(1.0 / 1000.0, g_realIntervalSeconds); }
    double EstimatedOutputFps()
    {
        if (g_outputIntervalSeconds > 0.0005)
            return 1.0 / g_outputIntervalSeconds;
        return 0.0;
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
extern "C" __declspec(dllexport) void __cdecl RimFG_SetTargetOutputFps(int fps) { RimFGPresent::SetTargetOutputFps(fps); }
extern "C" __declspec(dllexport) int __cdecl RimFG_GetTargetOutputFps() { return RimFGPresent::GetTargetOutputFps(); }
extern "C" __declspec(dllexport) double __cdecl RimFG_GetEstimatedBaseFps() { return RimFGPresent::EstimatedBaseFps(); }
extern "C" __declspec(dllexport) double __cdecl RimFG_GetEstimatedOutputFps() { return RimFGPresent::EstimatedOutputFps(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetGeneratedPresentCount() { return RimFGPresent::GeneratedPresentCount(); }
extern "C" __declspec(dllexport) unsigned long long __cdecl RimFG_GetSkippedPresentCount() { return RimFGPresent::SkippedPresentCount(); }
