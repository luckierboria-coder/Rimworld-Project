#pragma once

#include <array>
#include <atomic>
#include <d3d11.h>
#include <wrl/client.h>
#include "OpticalFlowBackend.h"

namespace RimFGFlow
{
    class GpuBudget
    {
    public:
        bool Initialize(ID3D11Device* device);
        void Shutdown();
        void Begin(ID3D11DeviceContext* context);
        void End(ID3D11DeviceContext* context);
        void Poll(ID3D11DeviceContext* context);

        QualityTier Tier() const { return tier_.load(std::memory_order_acquire); }
        double EmaMilliseconds() const { return emaMs_.load(std::memory_order_acquire); }

    private:
        struct Slot
        {
            Microsoft::WRL::ComPtr<ID3D11Query> disjoint;
            Microsoft::WRL::ComPtr<ID3D11Query> begin;
            Microsoft::WRL::ComPtr<ID3D11Query> end;
            bool issued = false;
        };

        void UpdateTier();

        std::array<Slot, 4> slots_{};
        int writeIndex_ = 0;
        bool initialized_ = false;
        bool haveEma_ = false;
        std::atomic<double> emaMs_{0.0};
        std::atomic<QualityTier> tier_{QualityTier::ResidualFlow};
    };
}
