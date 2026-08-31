#pragma once

#include <d3d11.h>
#include <dxgi.h>

namespace RimFGPresent
{
    // Installs one process-wide IDXGISwapChain::Present hook, but only treats
    // swapchains created on unityDevice as RimFG targets.
    bool Initialize(ID3D11Device* unityDevice);
    void Shutdown();

    bool IsInstalled();
    bool HasUnitySwapChain();
    IDXGISwapChain* GetUnitySwapChain(); // borrowed pointer; render/present thread only
}
