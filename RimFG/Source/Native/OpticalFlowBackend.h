#pragma once

#include <d3d11.h>
#include <wrl/client.h>

namespace RimFGFlow
{
    enum class QualityTier : int
    {
        Bypass = 0,
        CameraZoomOnly = 1,
        ResidualFlow = 2
    };

    struct MotionInput
    {
        float imageShiftX;
        float imageShiftY;
        float zoomScale;
        int width;
        int height;
    };

    class Backend
    {
    public:
        bool Initialize(ID3D11Device* device, int width, int height);
        void Shutdown();
        bool Resize(int width, int height);

        bool Dispatch(
            ID3D11DeviceContext* context,
            ID3D11ShaderResourceView* previous,
            ID3D11ShaderResourceView* current,
            const MotionInput& motion);

        ID3D11ShaderResourceView* FlowSrv() const { return flowSrv_.Get(); }
        int FlowWidth() const { return flowWidth_; }
        int FlowHeight() const { return flowHeight_; }

    private:
        bool EnsureShader();
        bool CreateResources();
        bool UploadConstants(ID3D11DeviceContext* context, const MotionInput& motion);

        ID3D11Device* device_ = nullptr;
        int width_ = 0;
        int height_ = 0;
        int flowWidth_ = 0;
        int flowHeight_ = 0;

        Microsoft::WRL::ComPtr<ID3D11Texture2D> flowTexture_;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> flowSrv_;
        Microsoft::WRL::ComPtr<ID3D11UnorderedAccessView> flowUav_;
        Microsoft::WRL::ComPtr<ID3D11ComputeShader> shader_;
        Microsoft::WRL::ComPtr<ID3D11Buffer> constants_;
    };
}
