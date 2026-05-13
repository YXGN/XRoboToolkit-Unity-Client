using UnityEngine;
using UnityEngine.XR;
using UnityEngine.UI;

public class ToggleCameraClippingPlane : MonoBehaviour
{
    // 三态显示模式：立体分眼、跟头SBS、空间锚定SBS
    private enum DisplayMode
    {
        StereoSplit = 0,
        HeadLockedSBS = 1,
        AnchorSBS = 2
    }

    [Header("Cameras to Adjust")]
    [SerializeField] private Camera firstCamera;
    [SerializeField] private Camera secondCamera;

    [Header("Clipping Plane Values")]
    [SerializeField] private float nearClipValueA = 0.1f;
    [SerializeField] private float nearClipValueB = 0.35f;
    [SerializeField] private bool keepNearClipToggle = false;

    [Header("RemoteCameraWindow")] public GameObject remoteCameraWindow;

    [Header("Display Mode Targets")]
    [SerializeField] private SetLERE setLere;
    [SerializeField] private RemoteCameraWindow remoteCameraWindowComp;
    [SerializeField] private GameObject headLockedSbsRoot;
    [SerializeField] private GameObject anchorSbsRoot;
    [SerializeField] private Transform anchorTransform;
    [SerializeField] private bool autoCreateAnchorSbsFromHeadLocked = true;
    [SerializeField] private float anchorDistanceFromHead = 1.2f;
    [SerializeField] private int anchorSbsCanvasSortingOrder = 320;
    [SerializeField] private float anchorFrameHeight = 0.9f;
    [SerializeField] private float anchorFrameLineWidth = 0.01f;
    [SerializeField] private Color anchorFrameColor = Color.white;
    [SerializeField] private Color anchorFrameFillColor = new Color(0f, 0f, 0f, 0.15f);
    [SerializeField] private RawImage headLockedSbsRawImage;
    [SerializeField] private RawImage anchorSbsRawImage;
    [SerializeField] private DisplayMode initialMode = DisplayMode.StereoSplit;

    private bool wasButtonPressed = false;
    private bool useValueA = true;
    private DisplayMode currentMode;
    private Transform runtimeAnchorTransform;
    private bool anchorSbsPipelineBoundLogged = false;
    private Material anchorSbsLeftHalfMaterial;

    private void Start()
    {
        // 兼容旧场景：尽量自动补全常用引用，减少手动拖拽成本。
        if (remoteCameraWindowComp == null && remoteCameraWindow != null)
        {
            remoteCameraWindowComp = remoteCameraWindow.GetComponent<RemoteCameraWindow>();
        }

        if (setLere == null && remoteCameraWindowComp != null)
        {
            setLere = remoteCameraWindowComp.GetComponent<SetLERE>();
        }

        if (headLockedSbsRawImage == null && remoteCameraWindowComp != null)
        {
            headLockedSbsRawImage = remoteCameraWindowComp.RemoteCameraImage;
        }

        // 若未手工指定跟头SBS根节点，则默认使用 RawImage 的父节点（通常是其Canvas）。
        if (headLockedSbsRoot == null && headLockedSbsRawImage != null && headLockedSbsRawImage.transform.parent != null)
        {
            headLockedSbsRoot = headLockedSbsRawImage.transform.parent.gameObject;
        }

        currentMode = initialMode;
        ApplyDisplayMode(currentMode, true);
    }

    void Update()
    {
        // 仅在远端相机窗口激活后允许切换，避免无流时误触发。
        if (remoteCameraWindow == null || !remoteCameraWindow.activeSelf) return;

        // Get the right controller directly using XRNode
        var rightControllerDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

        // Check if B button (secondaryButton) is pressed
        bool buttonValue = false;
        if (rightControllerDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out buttonValue) && buttonValue)
        {
            // 按下沿触发：按住不连发。
            if (!wasButtonPressed)
            {
                SwitchToNextMode();
            }

            wasButtonPressed = true;
        }
        else
        {
            wasButtonPressed = false;
        }

        // 纹理可能在开流后稍晚才可用，这里持续同步，确保两类SBS面板都拿到同一张解码纹理。
        SyncSbsTexture();
    }

    private void SwitchToNextMode()
    {
        currentMode = (DisplayMode)(((int)currentMode + 1) % 3);
        ApplyDisplayMode(currentMode, false);
    }

    private void ApplyDisplayMode(DisplayMode mode, bool forceApply)
    {
        LogWindow.Info($"显示模式切换请求: {mode} (forceApply={forceApply})");

        bool useStereoSplit = mode == DisplayMode.StereoSplit;
        bool useHeadLockedSbs = mode == DisplayMode.HeadLockedSBS;
        bool useAnchorSbs = mode == DisplayMode.AnchorSBS;

        // 若没有配置锚定SBS对象，则自动回退到跟头SBS，避免切换后无画面。
        if (useAnchorSbs && anchorSbsRoot == null)
        {
            TryCreateAnchorSbsRoot(true);
        }

        if (useAnchorSbs && anchorSbsRoot == null)
        {
            LogWindow.Error("AnchorSBS show failed: anchor root missing, fallback to HeadLockedSBS");
            currentMode = DisplayMode.HeadLockedSBS;
            useAnchorSbs = false;
            useHeadLockedSbs = true;
        }

        if (setLere != null)
        {
            setLere.SetStereoSplitActive(useStereoSplit);
        }

        if (headLockedSbsRoot != null)
        {
            headLockedSbsRoot.SetActive(useHeadLockedSbs);
        }

        if (anchorSbsRoot != null)
        {
            anchorSbsRoot.SetActive(useAnchorSbs);
        }

        if (useAnchorSbs)
        {
            anchorSbsPipelineBoundLogged = false;
            LogAnchorSbsPipelineStatus("mode-enter");
        }
        else
        {
            anchorSbsPipelineBoundLogged = false;
        }

        // 跟头SBS时打开跟随；空间锚定和立体分眼时关闭跟随。
        if (remoteCameraWindowComp != null)
        {
            remoteCameraWindowComp.SetFollowCamera(useHeadLockedSbs);
        }

        // 保留旧近裁剪切换能力（可选），避免影响历史调参流程。
        if (keepNearClipToggle && !forceApply)
        {
            float newClipValue = useValueA ? nearClipValueB : nearClipValueA;
            useValueA = !useValueA;
            ApplyNearClip(newClipValue);
        }

        SyncSbsTexture();
        LogWindow.Info($"显示模式已应用: {currentMode} (StereoSplit={useStereoSplit}, HeadLockedSBS={useHeadLockedSbs}, AnchorSBS={useAnchorSbs})");
    }

    private void SyncSbsTexture()
    {
        if (remoteCameraWindowComp == null || remoteCameraWindowComp.Texture == null)
        {
            return;
        }

        if (headLockedSbsRawImage != null && headLockedSbsRawImage.texture != remoteCameraWindowComp.Texture)
        {
            headLockedSbsRawImage.texture = remoteCameraWindowComp.Texture;
        }

        if (anchorSbsRawImage != null && anchorSbsRawImage.texture != remoteCameraWindowComp.Texture)
        {
            anchorSbsRawImage.texture = remoteCameraWindowComp.Texture;
            // 纹理绑定后恢复全不透明，避免占位态半透明影响图像观察。
            anchorSbsRawImage.color = Color.white;
        }

        if (anchorSbsRawImage != null && currentMode == DisplayMode.AnchorSBS)
        {
            // 双保险：即使材质采样失效，也通过 uvRect 强制裁切到左半幅。
            anchorSbsRawImage.uvRect = new Rect(0f, 0f, 0.5f, 1f);
        }

        if (anchorSbsRawImage != null && currentMode == DisplayMode.AnchorSBS)
        {
            // 防止运行期被其它逻辑改回整图采样，AnchorSBS 始终保持左半幅验证链路。
            anchorSbsRawImage.uvRect = new Rect(0f, 0f, 0.5f, 1f);
        }

        // AnchorSBS 模式下记录图像显示 pipeline 首次绑定成功日志，避免每帧刷屏。
        if (currentMode == DisplayMode.AnchorSBS && !anchorSbsPipelineBoundLogged)
        {
            bool textureBound = anchorSbsRawImage != null && anchorSbsRawImage.texture == remoteCameraWindowComp.Texture;
            if (textureBound)
            {
                anchorSbsPipelineBoundLogged = true;
                LogAnchorSbsPipelineStatus("texture-bound");
            }
        }
    }

    private void ApplyNearClip(float newClipValue)
    {
        if (firstCamera != null)
        {
            firstCamera.nearClipPlane = newClipValue;
        }

        if (secondCamera != null)
        {
            secondCamera.nearClipPlane = newClipValue;
        }

        Debug.Log($"Camera near clip plane changed to: {newClipValue}");
    }

    private void TryCreateAnchorSbsRoot(bool rebuildAnchorFromCurrentHeadPose)
    {
        if (!autoCreateAnchorSbsFromHeadLocked)
        {
            return;
        }

        if (rebuildAnchorFromCurrentHeadPose && !BuildRuntimeAnchorFromCurrentHeadPose())
        {
            return;
        }

        if (anchorSbsRoot != null)
        {
            Destroy(anchorSbsRoot);
            anchorSbsRoot = null;
            anchorSbsRawImage = null;
        }

        if (anchorTransform == null || headLockedSbsRoot == null)
        {
            return;
        }

        CreateAnchorPlaceholderFrame(anchorTransform);
    }

    /// <summary>
    /// 在锚点处创建空白占位画面框（1080:720，横向），后续可直接复用 RawImage 播放图传。
    /// </summary>
    private void CreateAnchorPlaceholderFrame(Transform parentAnchor)
    {
        if (parentAnchor == null)
        {
            return;
        }

        const float frameScale = 2f;
        float width = anchorFrameHeight * (1080f / 720f) * frameScale;
        float height = anchorFrameHeight * frameScale;

        anchorSbsRoot = new GameObject("AnchorSBS_FrameRoot", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        anchorSbsRoot.transform.SetParent(parentAnchor, false);
        anchorSbsRoot.transform.localPosition = Vector3.zero;
        anchorSbsRoot.transform.localRotation = Quaternion.identity;
        anchorSbsRoot.transform.localScale = Vector3.one * 0.001f;

        var rootRt = anchorSbsRoot.GetComponent<RectTransform>();
        rootRt.sizeDelta = new Vector2(width * 1000f, height * 1000f);

        var canvas = anchorSbsRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.sortingOrder = anchorSbsCanvasSortingOrder;

        var scaler = anchorSbsRoot.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        // 空白占位画面（后续直接挂图传纹理）
        GameObject rawObj = new GameObject("AnchorSBS_RawImage", typeof(RectTransform), typeof(RawImage));
        rawObj.transform.SetParent(anchorSbsRoot.transform, false);
        var rawRt = rawObj.GetComponent<RectTransform>();
        rawRt.anchorMin = new Vector2(0.5f, 0.5f);
        rawRt.anchorMax = new Vector2(0.5f, 0.5f);
        rawRt.pivot = new Vector2(0.5f, 0.5f);
        rawRt.sizeDelta = rootRt.sizeDelta;
        anchorSbsRawImage = rawObj.GetComponent<RawImage>();
        anchorSbsRawImage.color = anchorFrameFillColor;
        ApplyAnchorSbsLeftHalfMaterial(anchorSbsRawImage);

        // 四条边框线
        CreateFrameEdge(anchorSbsRoot.transform, new Vector2(0f, rootRt.sizeDelta.y * 0.5f), new Vector2(rootRt.sizeDelta.x, anchorFrameLineWidth * 1000f));   // top
        CreateFrameEdge(anchorSbsRoot.transform, new Vector2(0f, -rootRt.sizeDelta.y * 0.5f), new Vector2(rootRt.sizeDelta.x, anchorFrameLineWidth * 1000f)); // bottom
        CreateFrameEdge(anchorSbsRoot.transform, new Vector2(-rootRt.sizeDelta.x * 0.5f, 0f), new Vector2(anchorFrameLineWidth * 1000f, rootRt.sizeDelta.y)); // left
        CreateFrameEdge(anchorSbsRoot.transform, new Vector2(rootRt.sizeDelta.x * 0.5f, 0f), new Vector2(anchorFrameLineWidth * 1000f, rootRt.sizeDelta.y));  // right

        LogWindow.Info($"AnchorSBS: 已创建空白占位画面框 ratio=1080:720 scale=x{frameScale:F1} size=({width:F3},{height:F3})");
    }

    private bool BuildRuntimeAnchorFromCurrentHeadPose()
    {
        if (Camera.main == null)
        {
            LogWindow.Error("AnchorSBS 模式创建失败：未找到 Camera.main。");
            return false;
        }

        var head = Camera.main.transform;
        Vector3 anchorPos = head.position + head.forward * anchorDistanceFromHead;
        Quaternion anchorRot = Quaternion.LookRotation((head.position - anchorPos).normalized, Vector3.up);

        if (runtimeAnchorTransform != null)
        {
            Destroy(runtimeAnchorTransform.gameObject);
            runtimeAnchorTransform = null;
        }

        // 仅在切换到 AnchorSBS 时创建锚点宿主，避免启动即固定锚点。
        GameObject runtimeAnchorObj = new GameObject("AnchorSBS_RuntimePoint");
        runtimeAnchorTransform = runtimeAnchorObj.transform;
        runtimeAnchorTransform.SetPositionAndRotation(anchorPos, anchorRot);
        LogWindow.Info(
            $"AnchorSBS 开始创建锚点：pos={anchorPos.ToString("F3")}, rot={anchorRot.eulerAngles.ToString("F1")}");
        LogWindow.Info("AnchorSBS: runtime anchor host created");
        // 使用 Assembly-CSharp 内的宿主脚本写 LogWindow；SDK 内 PXR_SpatialAnchor 无法引用 LogWindow。
        runtimeAnchorObj.AddComponent<SpatialAnchorRuntimeHost>();

        anchorTransform = runtimeAnchorTransform;
        return true;
    }

    /// <summary>
    /// 记录 AnchorSBS 图像显示 pipeline 关键链路状态：
    /// MediaDecoder -> RemoteCameraWindow.Texture -> AnchorSBS RawImage.texture
    /// </summary>
    private void LogAnchorSbsPipelineStatus(string stage)
    {
        string decoderTex = (remoteCameraWindowComp != null && remoteCameraWindowComp.Texture != null)
            ? $"{remoteCameraWindowComp.Texture.width}x{remoteCameraWindowComp.Texture.height}"
            : "null";
        string rawTex = (anchorSbsRawImage != null && anchorSbsRawImage.texture != null)
            ? $"{anchorSbsRawImage.texture.width}x{anchorSbsRawImage.texture.height}"
            : "null";
        bool sameRef = anchorSbsRawImage != null
                       && remoteCameraWindowComp != null
                       && remoteCameraWindowComp.Texture != null
                       && ReferenceEquals(anchorSbsRawImage.texture, remoteCameraWindowComp.Texture);

        LogWindow.Info(
            $"AnchorSBS pipeline[{stage}]: decoderTex={decoderTex}, anchorRawTex={rawTex}, sameRef={sameRef}, anchorRootActive={(anchorSbsRoot != null && anchorSbsRoot.activeSelf)}, leftHalfMat={(anchorSbsRawImage != null && anchorSbsRawImage.material != null ? anchorSbsRawImage.material.shader.name : "null")}");
    }

    /// <summary>
    /// 给 AnchorSBS 的 RawImage 挂固定“左半幅采样”材质，先验证上屏链路。
    /// </summary>
    private void ApplyAnchorSbsLeftHalfMaterial(RawImage rawImage)
    {
        if (rawImage == null)
        {
            return;
        }

        if (anchorSbsLeftHalfMaterial == null)
        {
            Shader shader = Shader.Find("UI/AnchorSBSLeftHalf");
            if (shader == null)
            {
                LogWindow.Error("AnchorSBS: 未找到 Shader UI/AnchorSBSLeftHalf，保持默认材质。");
                return;
            }

            anchorSbsLeftHalfMaterial = new Material(shader);
            anchorSbsLeftHalfMaterial.name = "AnchorSBS_LeftHalf_RuntimeMat";
        }

        rawImage.material = anchorSbsLeftHalfMaterial;
        rawImage.uvRect = new Rect(0f, 0f, 0.5f, 1f);
        LogWindow.Info("AnchorSBS: 已挂载左半幅采样材质，并设置 uvRect=left-half（双保险）");
    }

    /// <summary>
    /// 创建边框线（Image），用于空白占位画面框。
    /// </summary>
    private void CreateFrameEdge(Transform parent, Vector2 anchoredPos, Vector2 size)
    {
        GameObject edgeObj = new GameObject("AnchorSBS_FrameEdge", typeof(RectTransform), typeof(Image));
        edgeObj.transform.SetParent(parent, false);
        var edgeRt = edgeObj.GetComponent<RectTransform>();
        edgeRt.anchorMin = new Vector2(0.5f, 0.5f);
        edgeRt.anchorMax = new Vector2(0.5f, 0.5f);
        edgeRt.pivot = new Vector2(0.5f, 0.5f);
        edgeRt.anchoredPosition = anchoredPos;
        edgeRt.sizeDelta = size;

        var edgeImage = edgeObj.GetComponent<Image>();
        edgeImage.color = anchorFrameColor;
    }
}
