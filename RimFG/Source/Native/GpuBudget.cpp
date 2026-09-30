#include "GpuBudget.h"

namespace RimFGFlow
{
    bool GpuBudget::Initialize(ID3D11Device* device)
    {
        if (!device) return false;
        Shutdown();
        for (auto& s : slots_)
        {
            D3D11_QUERY_DESC q{};
            q.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
            if (FAILED(device->CreateQuery(&q, &s.disjoint))) return false;
            q.Query = D3D11_QUERY_TIMESTAMP;
            if (FAILED(device->CreateQuery(&q, &s.begin))) return false;
            if (FAILED(device->CreateQuery(&q, &s.end))) return false;
        }
        initialized_ = true;
        tier_.store(QualityTier::ResidualFlow, std::memory_order_release);
        return true;
    }

    void GpuBudget::Shutdown()
    {
        for (auto& s : slots_)
        {
            s.disjoint.Reset(); s.begin.Reset(); s.end.Reset(); s.issued = false;
        }
        initialized_ = false;
        haveEma_ = false;
        emaMs_.store(0.0, std::memory_order_release);
        writeIndex_ = 0;
        tier_.store(QualityTier::ResidualFlow, std::memory_order_release);
    }

    void GpuBudget::Begin(ID3D11DeviceContext* context)
    {
        if (!initialized_ || !context) return;
        Slot& s = slots_[writeIndex_];
        context->Begin(s.disjoint.Get());
        context->End(s.begin.Get());
    }

    void GpuBudget::End(ID3D11DeviceContext* context)
    {
        if (!initialized_ || !context) return;
        Slot& s = slots_[writeIndex_];
        context->End(s.end.Get());
        context->End(s.disjoint.Get());
        s.issued = true;
        writeIndex_ = (writeIndex_ + 1) % static_cast<int>(slots_.size());
    }

    void GpuBudget::Poll(ID3D11DeviceContext* context)
    {
        if (!initialized_ || !context) return;
        const int readIndex = (writeIndex_ + 1) % static_cast<int>(slots_.size());
        Slot& s = slots_[readIndex];
        if (!s.issued) return;

        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint{};
        UINT64 begin = 0, end = 0;
        if (context->GetData(s.disjoint.Get(), &disjoint, sizeof(disjoint), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) return;
        if (context->GetData(s.begin.Get(), &begin, sizeof(begin), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) return;
        if (context->GetData(s.end.Get(), &end, sizeof(end), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) return;
        s.issued = false;
        if (disjoint.Disjoint || disjoint.Frequency == 0 || end <= begin) return;

        const double ms = (static_cast<double>(end - begin) * 1000.0) / static_cast<double>(disjoint.Frequency);
        const double previous = emaMs_.load(std::memory_order_relaxed);
        const double next = haveEma_ ? (previous * 0.85 + ms * 0.15) : ms;
        emaMs_.store(next, std::memory_order_release);
        haveEma_ = true;
        UpdateTier();
    }

    void GpuBudget::UpdateTier()
    {
        const double ema = emaMs_.load(std::memory_order_acquire);
        const QualityTier current = tier_.load(std::memory_order_acquire);
        if (current == QualityTier::ResidualFlow)
        {
            if (ema > 3.5)
                tier_.store(QualityTier::CameraZoomOnly, std::memory_order_release);
        }
        else
        {
            tier_.store(ema < 2.6 ? QualityTier::ResidualFlow : QualityTier::CameraZoomOnly, std::memory_order_release);
        }
    }
}
