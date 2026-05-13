using System;
using Unity.XR.PXR;
using UnityEngine;

/// <summary>
/// 运行时空间锚点宿主（Assembly-CSharp），用于在头显控制面板的 LogWindow 中输出成功/失败原因。
/// 避免在 Unity.XR.PICO 程序集内直接引用 LogWindow（跨程序集不可引用）。
/// </summary>
[DisallowMultipleComponent]
public class SpatialAnchorRuntimeHost : MonoBehaviour
{
    [HideInInspector] public bool Created;
    [HideInInspector] public ulong anchorHandle;
    [HideInInspector] public Guid anchorUuid;

    private async void Start()
    {
        LogWindow.Info($"SpatialAnchorRuntimeHost: 开始异步创建锚点 pos={transform.position:F3}");

        // 未启用 AR Foundation 时，PXR_AnchorSubsystem 不会自动启动 SpatialAnchor 的 Sense Data Provider；
        // 若不先 StartSenseDataProvider，CreateSpatialAnchorAsync 常见返回 ERROR_VALIDATION_FAILURE。
        var providerStart = await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);
        LogWindow.Info($"SpatialAnchorRuntimeHost: SpatialAnchor SenseDataProvider 启动结果={providerStart}");
        if (providerStart != PxrResult.SUCCESS)
        {
            LogWindow.Error(
                $"SpatialAnchorRuntimeHost: SenseDataProvider 启动失败，跳过创建锚点 result={providerStart}");
            Destroy(gameObject);
            return;
        }

        var stateRet = PXR_MixedReality.GetSenseDataProviderState(PxrSenseDataProviderType.SpatialAnchor, out var providerState);
        LogWindow.Info($"SpatialAnchorRuntimeHost: Provider 状态查询={stateRet}, state={providerState}");

        var result = await PXR_MixedReality.CreateSpatialAnchorAsync(transform.position, transform.rotation);
        if (result.result == PxrResult.SUCCESS)
        {
            anchorHandle = result.anchorHandle;
            anchorUuid = result.uuid;
            Created = true;
            LogWindow.Info($"SpatialAnchorRuntimeHost: 锚点创建成功 handle={anchorHandle}, uuid={anchorUuid}");
        }
        else
        {
            var hint = result.result.ToString().Contains("VALIDATION", StringComparison.OrdinalIgnoreCase)
                ? "（若已启动 Provider 仍失败：检查 6DoF 追踪、安全区、VST/MR 就绪后再试）"
                : string.Empty;
            LogWindow.Error($"SpatialAnchorRuntimeHost: 锚点创建失败 result={result.result}{hint}");
            Destroy(gameObject);
        }
    }

    private void Update()
    {
        if (!Created)
        {
            return;
        }

        var locate = PXR_MixedReality.LocateAnchor(anchorHandle, out var position, out var rotation);
        if (locate == PxrResult.SUCCESS)
        {
            transform.SetPositionAndRotation(position, rotation);
        }
    }

    private void OnDestroy()
    {
        if (!Created || anchorHandle == 0)
        {
            return;
        }

        var result = PXR_MixedReality.DestroyAnchor(anchorHandle);
        if (result != PxrResult.SUCCESS)
        {
            LogWindow.Error("SpatialAnchorRuntimeHost: DestroyAnchor 失败 " + result);
        }
    }
}
