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
    // AnchorSBS 右眼（RE）分眼扩展
    private RawImage anchorSbsRawImageRE;
    private Material anchorSbsRightHalfMaterial;

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
        if (remoteCameraWindowComp == null || remoteCameraWindowComp.Texture == null) return;
        var tex = remoteCameraWindowComp.Texture;

        // ── 跟头 SBS ─────────────────────────────────────────────────────────
        if (headLockedSbsRawImage != null && !ReferenceEquals(headLockedSbsRawImage.texture, tex))
            headLockedSbsRawImage.texture = tex;

        // ── AnchorSBS 左眼 ────────────────────────────────────────────────────
        if (anchorSbsRawImage != null && !ReferenceEquals(anchorSbsRawImage.texture, tex))
        {
            anchorSbsRawImage.texture = tex;
            anchorSbsRawImage.color   = Color.white;
        }
        // Custom/SampleRT 通过 _mainRT 采样纹理，需同步给材质
        if (anchorSbsLeftHalfMaterial != null)
            anchorSbsLeftHalfMaterial.SetTexture("_mainRT", tex);

        // ── AnchorSBS 右眼 ────────────────────────────────────────────────────
        if (anchorSbsRawImageRE != null && !ReferenceEquals(anchorSbsRawImageRE.texture, tex))
        {
            anchorSbsRawImageRE.texture = tex;
            anchorSbsRawImageRE.color   = Color.white;
        }
        if (anchorSbsRightHalfMaterial != null)
            anchorSbsRightHalfMaterial.SetTexture("_mainRT", tex);

        // ── 首次双眼纹理绑定成功日志（避免每帧刷屏）────────────────────────
        if (currentMode == DisplayMode.AnchorSBS && !anchorSbsPipelineBoundLogged)
        {
            bool leBound = anchorSbsRawImage   != null && ReferenceEquals(anchorSbsRawImage.texture,   tex);
            bool reBound = anchorSbsRawImageRE != null && ReferenceEquals(anchorSbsRawImageRE.texture, tex);
            if (leBound && reBound)
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
            anchorSbsRawImageRE = null;
            anchorSbsLeftHalfMaterial = null;
            anchorSbsRightHalfMaterial = null;
        }

        if (anchorTransform == null || headLockedSbsRoot == null)
        {
            return;
        }

        CreateAnchorPlaceholderFrame(anchorTransform);
    }

    /// <summary>
    /// 在锚点处创建双目分眼画布（左眼/右眼各一个 WorldSpace Canvas）。
    /// 左眼画布挂载与 SetLERE.CanvLE 相同图层，右眼画布与 CanvRE 相同图层；
    /// 分别使用 Custom/SampleRT (_isLE=1/0) 采样 SBS 纹理左/右半幅，
    /// 实现空间锚定画布上的立体分眼图传显示。
    /// </summary>
    private void CreateAnchorPlaceholderFrame(Transform parentAnchor)
    {
        if (parentAnchor == null) return;

        const float frameScale = 2f;
        float width  = anchorFrameHeight * (1080f / 720f) * frameScale;
        float height = anchorFrameHeight * frameScale;
        Vector2 canvasSizeDelta = new Vector2(width * 1000f, height * 1000f);

        // 从 SetLERE 的现有画布继承左右眼图层，保证与 StereoSplit 模式图层配置完全一致
        int leLayer = (setLere != null && setLere.CanvLE != null) ? setLere.CanvLE.layer : 0;
        int reLayer = (setLere != null && setLere.CanvRE != null) ? setLere.CanvRE.layer : 0;

        // 顶层空容器（不含 Canvas），挂在锚点下，统一控制双目画布的显隐
        anchorSbsRoot = new GameObject("AnchorSBS_Root");
        anchorSbsRoot.transform.SetParent(parentAnchor, false);
        anchorSbsRoot.transform.localPosition = Vector3.zero;
        anchorSbsRoot.transform.localRotation = Quaternion.identity;
        anchorSbsRoot.transform.localScale    = Vector3.one;

        // ── 左眼 Canvas（仅左眼摄像机图层可见）──────────────────────────────
        GameObject leCanvasObj = CreateEyeCanvas("AnchorSBS_Canvas_LE", anchorSbsRoot.transform, leLayer, canvasSizeDelta);
        anchorSbsRawImage       = CreateEyeRawImage("AnchorSBS_RawImage_LE", leCanvasObj.transform, canvasSizeDelta, leLayer);
        anchorSbsRawImage.color = anchorFrameFillColor;

        // ── 右眼 Canvas（仅右眼摄像机图层可见）──────────────────────────────
        GameObject reCanvasObj  = CreateEyeCanvas("AnchorSBS_Canvas_RE", anchorSbsRoot.transform, reLayer, canvasSizeDelta);
        anchorSbsRawImageRE       = CreateEyeRawImage("AnchorSBS_RawImage_RE", reCanvasObj.transform, canvasSizeDelta, reLayer);
        anchorSbsRawImageRE.color = anchorFrameFillColor;

        // 为双目 RawImage 建立 Custom/SampleRT 材质，_isLE 决定采样左/右半幅
        SetupAnchorSbsMaterials(anchorSbsRawImage, anchorSbsRawImageRE);

        // 各自画布上绘制边框线（保证双目均能看见边框）
        CreateFrameEdgesOnCanvas(leCanvasObj.transform, canvasSizeDelta, leLayer);
        CreateFrameEdgesOnCanvas(reCanvasObj.transform, canvasSizeDelta, reLayer);

        LogWindow.Info(
            $"AnchorSBS: 已创建双目分眼锚定画布 leLayer={leLayer}, reLayer={reLayer}, " +
            $"size=({width:F3}m,{height:F3}m) scale=x{frameScale:F1}");
    }

    /// <summary>创建单眼用 WorldSpace Canvas 节点。</summary>
    private GameObject CreateEyeCanvas(string name, Transform parent, int layer, Vector2 sizeDelta)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        obj.layer = layer;
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = Vector3.zero;
        obj.transform.localRotation = Quaternion.identity;
        obj.transform.localScale    = Vector3.one * 0.001f;

        var rt = obj.GetComponent<RectTransform>();
        rt.sizeDelta = sizeDelta;

        var canvas = obj.GetComponent<Canvas>();
        canvas.renderMode   = RenderMode.WorldSpace;
        canvas.worldCamera  = Camera.main;
        canvas.sortingOrder = anchorSbsCanvasSortingOrder;

        var scaler = obj.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        return obj;
    }

    /// <summary>创建单眼用 RawImage 节点，居中填满父 Canvas。</summary>
    private RawImage CreateEyeRawImage(string name, Transform parent, Vector2 sizeDelta, int layer)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(RawImage));
        obj.layer = layer;
        obj.transform.SetParent(parent, false);

        var rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = sizeDelta;

        return obj.GetComponent<RawImage>();
    }

    /// <summary>
    /// 为 AnchorSBS 双目 RawImage 建立运行时材质。
    /// 优先使用 Custom/SampleRT（与 SetLERE 完全一致的分眼采样逻辑），
    /// Shader 缺失时降级为简单 uvRect 裁切。
    /// </summary>
    private void SetupAnchorSbsMaterials(RawImage leImage, RawImage reImage)
    {
        if (leImage == null || reImage == null) return;

        Shader sampleShader = Shader.Find("Custom/SampleRT");
        if (sampleShader != null)
        {
            // 左眼：_isLE=1，采样 SBS 纹理左半幅
            anchorSbsLeftHalfMaterial = new Material(sampleShader) { name = "AnchorSBS_LE_RuntimeMat" };
            anchorSbsLeftHalfMaterial.SetInt("_isLE", 1);
            CopyAnchorSbsShaderParams(anchorSbsLeftHalfMaterial, setLere != null ? setLere.matLE : null);
            leImage.material = anchorSbsLeftHalfMaterial;

            // 右眼：_isLE=0，采样 SBS 纹理右半幅
            anchorSbsRightHalfMaterial = new Material(sampleShader) { name = "AnchorSBS_RE_RuntimeMat" };
            anchorSbsRightHalfMaterial.SetInt("_isLE", 0);
            CopyAnchorSbsShaderParams(anchorSbsRightHalfMaterial, setLere != null ? setLere.matRE : null);
            reImage.material = anchorSbsRightHalfMaterial;

            LogWindow.Info("AnchorSBS: 已创建 Custom/SampleRT 双目运行时材质 (LE _isLE=1 / RE _isLE=0)");
        }
        else
        {
            // 降级：uvRect 直接裁切左/右半幅
            leImage.uvRect = new Rect(0f,   0f, 0.5f, 1f);
            reImage.uvRect = new Rect(0.5f, 0f, 0.5f, 1f);
            LogWindow.Error("AnchorSBS: Custom/SampleRT Shader 未找到，已降级为 uvRect 模式（LE=左0~0.5，RE=右0.5~1）");
        }
    }

    /// <summary>从 SetLERE 现有材质拷贝 SBS shader 参数到目标材质；src 为 null 时使用默认值。</summary>
    private void CopyAnchorSbsShaderParams(Material dst, Material src)
    {
        float visibleRatio    = 0.555f;
        float contentRatio    = 1.8f;
        float heightCompress  = 1.333333f;

        if (src != null)
        {
            try
            {
                visibleRatio   = src.GetFloat("_visibleRatio");
                contentRatio   = src.GetFloat("_contentRatio");
                heightCompress = src.GetFloat("_heightCompressionFactor");
            }
            catch { /* 材质属性缺失时保持默认值 */ }
        }

        dst.SetFloat("_visibleRatio",          visibleRatio);
        dst.SetFloat("_contentRatio",           contentRatio);
        dst.SetFloat("_heightCompressionFactor", heightCompress);
    }

    /// <summary>在指定 Canvas 下创建四条边框线，图层与 Canvas 一致。</summary>
    private void CreateFrameEdgesOnCanvas(Transform canvasTransform, Vector2 sizeDelta, int layer)
    {
        CreateFrameEdgeOnLayer(canvasTransform, new Vector2(0f,              sizeDelta.y * 0.5f),  new Vector2(sizeDelta.x, anchorFrameLineWidth * 1000f), layer); // top
        CreateFrameEdgeOnLayer(canvasTransform, new Vector2(0f,             -sizeDelta.y * 0.5f),  new Vector2(sizeDelta.x, anchorFrameLineWidth * 1000f), layer); // bottom
        CreateFrameEdgeOnLayer(canvasTransform, new Vector2(-sizeDelta.x * 0.5f, 0f),              new Vector2(anchorFrameLineWidth * 1000f, sizeDelta.y), layer); // left
        CreateFrameEdgeOnLayer(canvasTransform, new Vector2( sizeDelta.x * 0.5f, 0f),              new Vector2(anchorFrameLineWidth * 1000f, sizeDelta.y), layer); // right
    }

    private void CreateFrameEdgeOnLayer(Transform parent, Vector2 anchoredPos, Vector2 size, int layer)
    {
        var edgeObj = new GameObject("AnchorSBS_FrameEdge", typeof(RectTransform), typeof(Image));
        edgeObj.layer = layer;
        edgeObj.transform.SetParent(parent, false);
        var rt = edgeObj.GetComponent<RectTransform>();
        rt.anchorMin      = new Vector2(0.5f, 0.5f);
        rt.anchorMax      = new Vector2(0.5f, 0.5f);
        rt.pivot          = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta      = size;
        edgeObj.GetComponent<Image>().color = anchorFrameColor;
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
    /// 记录 AnchorSBS 双目图像显示 pipeline 关键链路状态：
    /// MediaDecoder → RemoteCameraWindow.Texture → LE/RE RawImage.texture → LE/RE Material._mainRT
    /// </summary>
    private void LogAnchorSbsPipelineStatus(string stage)
    {
        string decoderTex = (remoteCameraWindowComp != null && remoteCameraWindowComp.Texture != null)
            ? $”{remoteCameraWindowComp.Texture.width}x{remoteCameraWindowComp.Texture.height}” : “null”;
        string leTex = (anchorSbsRawImage != null && anchorSbsRawImage.texture != null)
            ? $”{anchorSbsRawImage.texture.width}x{anchorSbsRawImage.texture.height}” : “null”;
        string reTex = (anchorSbsRawImageRE != null && anchorSbsRawImageRE.texture != null)
            ? $”{anchorSbsRawImageRE.texture.width}x{anchorSbsRawImageRE.texture.height}” : “null”;
        bool leRef = anchorSbsRawImage   != null && remoteCameraWindowComp?.Texture != null
                     && ReferenceEquals(anchorSbsRawImage.texture,   remoteCameraWindowComp.Texture);
        bool reRef = anchorSbsRawImageRE != null && remoteCameraWindowComp?.Texture != null
                     && ReferenceEquals(anchorSbsRawImageRE.texture, remoteCameraWindowComp.Texture);

        LogWindow.Info(
            $”AnchorSBS pipeline[{stage}]: decoderTex={decoderTex}, “ +
            $”LE={leTex}(ref={leRef}, mat={ShaderName(anchorSbsLeftHalfMaterial)}), “ +
            $”RE={reTex}(ref={reRef}, mat={ShaderName(anchorSbsRightHalfMaterial)}), “ +
            $”rootActive={(anchorSbsRoot != null && anchorSbsRoot.activeSelf)}”);
    }

    private static string ShaderName(Material m) => m != null ? m.shader.name : “null”;
}
