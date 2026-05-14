using UnityEngine;
using UnityEngine.Rendering;
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

    [Tooltip("为 true 时 AnchorSBS 画布每帧朝向头显（解决 LocateAnchor 旋转与 Quad 正面不一致导致整面被背面剔除看不见）。真正世界锁定时可关掉。")]
    [SerializeField] private bool anchorSbsBillboardTowardsHead = true;

    [Tooltip("URP 下 MeshRenderer 使用 Built-in CG 的 Custom/SampleRT 常无法参与前向渲染（全透明/不画）；诊断纯色默认走 URP Unlit。接真实图传时再关并改用 SampleRT。")]
    [SerializeField] private bool anchorSbsDiagUseUrpUnlitSolid = true;

    private static readonly string[] AnchorUrpDiagShaderCandidates =
    {
        "Universal Render Pipeline/Unlit",
        "Universal Render Pipeline/Simple Lit",
    };

    private bool wasButtonPressed = false;
    private bool useValueA = true;
    private DisplayMode currentMode;
    private Transform runtimeAnchorTransform;
    private bool anchorSbsPipelineBoundLogged = false;
    private Material anchorSbsLeftHalfMaterial;
    // AnchorSBS 右眼（RE）分眼扩展
    private RawImage anchorSbsRawImageRE;
    private Material anchorSbsRightHalfMaterial;

    // 与 SetLERE.cs 中私有字段默认值一致（matLE 尚未写入时用于 Anchor 诊断）
    private const float AnchorRtVisibleRatioDefault = 0.555f;
    private const float AnchorRtContentRatioDefault = 1.8f;
    private const float AnchorRtHeightCompressionDefault = 1.333333f;

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

        // AnchorSBS_Root：位置跟锚点；旋转默认 billboard 朝头显（PICO LocateAnchor 的旋转常与 Quad 正面不一致 → 背面剔除看不见）。
        if (currentMode == DisplayMode.AnchorSBS &&
            anchorSbsRoot != null &&
            runtimeAnchorTransform != null)
        {
            var host = runtimeAnchorTransform.GetComponent<SpatialAnchorRuntimeHost>();
            if (host != null && host.Created)
            {
                anchorSbsRoot.transform.position = runtimeAnchorTransform.position;
                if (!anchorSbsBillboardTowardsHead)
                {
                    anchorSbsRoot.transform.rotation = runtimeAnchorTransform.rotation;
                }
            }

            if (anchorSbsBillboardTowardsHead && TryGetStereoEyeMidpoint(out Vector3 eyeMid))
            {
                Vector3 towardCam = eyeMid - anchorSbsRoot.transform.position;
                if (towardCam.sqrMagnitude > 1e-4f)
                {
                    anchorSbsRoot.transform.rotation = Quaternion.LookRotation(towardCam, Vector3.up);
                }
            }
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

        // ── AnchorSBS 纹理同步（诊断期间暂时注释，EyeTest Shader 不需要纹理）──
        // if (anchorSbsRawImage != null && !ReferenceEquals(anchorSbsRawImage.texture, tex))
        // {
        //     anchorSbsRawImage.texture = tex;
        //     anchorSbsRawImage.color   = Color.white;
        // }
        // ── 诊断结束后取消注释，恢复真实图传纹理绑定 ──────────────────────

        // ── 首次纹理绑定成功日志（避免每帧刷屏）────────────────────────────
        if (currentMode == DisplayMode.AnchorSBS && !anchorSbsPipelineBoundLogged)
        {
            if (anchorSbsRawImage != null && ReferenceEquals(anchorSbsRawImage.texture, tex))
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
    /// 在锚点处创建两个 3D Quad（MeshRenderer），与 SetLERE 的 CanvLE/CanvRE 完全同构：
    /// - LE Quad：与 CanvLE 同图层，Camera Culling Mask 只允许左眼相机看到
    /// - RE Quad：与 CanvRE 同图层，Camera Culling Mask 只允许右眼相机看到
    /// [诊断模式] Custom/SampleRT + 1×1 纯色 _mainRT（左眼蓝 / 右眼红），参数与 SetLERE 一致。
    /// </summary>
    private void CreateAnchorPlaceholderFrame(Transform parentAnchor)
    {
        if (parentAnchor == null) return;

        const float frameScale = 2f;
        float width  = anchorFrameHeight * (1080f / 720f) * frameScale;
        float height = anchorFrameHeight * frameScale;

        // 从 SetLERE 继承图层，确保与正在工作的 StereoSplit 图层隔离完全一致
        int leLayer = (setLere != null && setLere.CanvLE != null) ? setLere.CanvLE.layer : 0;
        int reLayer = (setLere != null && setLere.CanvRE != null) ? setLere.CanvRE.layer : 0;
        LogWindow.Info($"AnchorSBS: 继承图层 leLayer={leLayer}({LayerMask.LayerToName(leLayer)}), " +
                       $"reLayer={reLayer}({LayerMask.LayerToName(reLayer)})");

        // [诊断日志] 打印摄像机 Culling Mask，确认 leLayer/reLayer 是否被渲染
        LogCameraCullingMask(leLayer, reLayer);

        // 父容器（纯空 GameObject，SetActive 统一控制两个 Quad 显隐）
        // 注意：不挂在 parentAnchor 的层级下，而是独立放在场景根，
        // 由 Update() 主动跟随锚点位置。
        // 原因：SpatialAnchorRuntimeHost 锚点创建失败时会 Destroy(gameObject)，
        // 若 AnchorSBS_Root 是其子节点，会被一并销毁导致画布消失。
        anchorSbsRoot = new GameObject("AnchorSBS_Root");
        anchorSbsRoot.transform.SetPositionAndRotation(parentAnchor.position, parentAnchor.rotation);
        anchorSbsRoot.transform.localScale = Vector3.one;

        // [诊断] LE Quad → 蓝色，RE Quad → 红色；微小 local X 偏移减轻同层 Z-fighting
        const float diagHalfSeparation = 0.012f;
        anchorSbsLeftHalfMaterial  = CreateAnchorDiagQuad(
            "AnchorSBS_Quad_LE", anchorSbsRoot.transform, leLayer,
            new Vector3(width, height, 1f), Color.blue, isLE: true,
            new Vector3(-diagHalfSeparation, 0f, 0f));

        anchorSbsRightHalfMaterial = CreateAnchorDiagQuad(
            "AnchorSBS_Quad_RE", anchorSbsRoot.transform, reLayer,
            new Vector3(width, height, 1f), Color.red, isLE: false,
            new Vector3(diagHalfSeparation, 0f, 0f));

        // 不再使用 RawImage
        anchorSbsRawImage   = null;
        anchorSbsRawImageRE = null;

        LogWindow.Info(
            $"AnchorSBS [诊断]: 已创建 3D Quad 双目画布（左眼=蓝 / 右眼=红）" +
            $" size=({width:F3}m,{height:F3}m)");
        LogAnchorSbsDiagStepSummary(leLayer, reLayer);
    }

    /// <summary>分步调试：创建完成后在 LogWindow 中打印核对清单。</summary>
    private void LogAnchorSbsDiagStepSummary(int leLayer, int reLayer)
    {
        bool sameLayer = leLayer == reLayer;
        LogWindow.Info(
            "AnchorSBS [debug 步骤]\n" +
            "1) 切到 AnchorSBS 后应看到蓝/红；若全黑先看 SampleRT 的 _visibleRatio/_contentRatio 是否与 SetLERE 一致。\n" +
            "2) 看 Log 中 CullMask：LeftCamera(firstCam) 须包含 leLayer，RightCamera(secondCam) 须包含 reLayer。\n" +
            "3) 若 leLayer==reLayer，两眼都会画两片 mesh，已做微小 X 分离减轻 Z-fight；生产环境建议左右眼专用 Layer。\n" +
            "4) 若 CullMask 正常仍看不见：多为 LocateAnchor 的旋转使 Quad 背向头显（背面剔除）；默认已开启 anchorSbsBillboardTowardsHead 每帧朝头显。\n" +
            "5) 项目为 URP 时 Mesh 上勿用 Built-in CG 的 Custom/SampleRT 做诊断；默认 anchorSbsDiagUseUrpUnlitSolid 走 URP Unlit 纯色。\n" +
            $"当前 leLayer={leLayer} reLayer={reLayer} sameLayer={sameLayer}");
    }

    /// <summary>与 SetLERE 一致：SetInt(_isLE) + 比例参数（避免 SampleRT UV 裁剪成全透明）。</summary>
    private void ApplyAnchorSampleRtStereoParams(Material mat, bool isLE)
    {
        if (mat == null) return;
        mat.SetInt("_isLE", isLE ? 1 : 0);
        float vr = AnchorRtVisibleRatioDefault;
        float cr = AnchorRtContentRatioDefault;
        float hcf = AnchorRtHeightCompressionDefault;
        if (setLere != null && setLere.matLE != null)
        {
            vr = setLere.matLE.GetFloat("_visibleRatio");
            cr = setLere.matLE.GetFloat("_contentRatio");
            hcf = setLere.matLE.GetFloat("_heightCompressionFactor");
        }

        mat.SetFloat("_visibleRatio", vr);
        mat.SetFloat("_contentRatio", cr);
        mat.SetFloat("_heightCompressionFactor", hcf);
    }

    private bool TryGetStereoEyeMidpoint(out Vector3 midpoint)
    {
        if (firstCamera != null && secondCamera != null)
        {
            midpoint = (firstCamera.transform.position + secondCamera.transform.position) * 0.5f;
            return true;
        }

        var cam = GetAnchorBillboardCamera();
        if (cam != null)
        {
            midpoint = cam.transform.position;
            return true;
        }

        midpoint = default;
        return false;
    }

    /// <summary>URP 下用引擎自带 Unlit/SimpleLit 做纯色诊断（Mesh 上可渲染）；SampleRT 为 Built-in CG，在 URP MeshRenderer 上常完全不显示。</summary>
    private bool TryCreateAnchorUrpUnlitSolidMaterial(string matName, Color diagColor, out Material mat)
    {
        mat = null;
        if (!anchorSbsDiagUseUrpUnlitSolid)
        {
            return false;
        }

        foreach (var shaderPath in AnchorUrpDiagShaderCandidates)
        {
            var s = Shader.Find(shaderPath);
            if (s == null)
            {
                continue;
            }

            mat = new Material(s) { name = matName };
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", diagColor);
            }
            else if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", diagColor);
            }

            if (mat.HasProperty("_Cull"))
            {
                mat.SetFloat("_Cull", 0f);
            }

            LogWindow.Info($"AnchorSBS: 诊断材质使用 URP shader={shaderPath}");
            return true;
        }

        var spriteShader = Shader.Find("Sprites/Default");
        if (spriteShader != null)
        {
            mat = new Material(spriteShader) { name = matName };
            if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", diagColor);
            }
            else
            {
                mat.color = diagColor;
            }

            LogWindow.Warn("AnchorSBS: 未找到 URP Unlit，诊断材质回退 Sprites/Default（请确认 Package 含 URP）。");
            return true;
        }

        LogWindow.Error(
            "AnchorSBS: 未找到 URP Unlit/SimpleLit 且 Sprites/Default 失败，将回退 Custom/SampleRT；" +
            "在 URP 下 Mesh 可能仍不可见。");
        return false;
    }

    /// <summary>
    /// 创建一个 3D Quad（MeshRenderer），挂指定图层。
    /// 诊断：URP 下默认用 URP Unlit 纯色（与 MeshRenderer 兼容）；否则回退 Custom/SampleRT + 1×1 纹理。
    /// </summary>
    private Material CreateAnchorDiagQuad(string name, Transform parent, int layer,
                                           Vector3 scale, Color diagColor, bool isLE, Vector3 localOffset)
    {
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name  = name;
        quad.layer = layer;
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = localOffset;
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale    = scale;

        Destroy(quad.GetComponent<MeshCollider>());

        var meshRenderer = quad.GetComponent<MeshRenderer>();
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;

        if (TryCreateAnchorUrpUnlitSolidMaterial($"{name}_DiagMat", diagColor, out Material urpMat))
        {
            meshRenderer.material = urpMat;
            LogWindow.Info(
                $"AnchorSBS [诊断]: {name} path=URP-Solid shader={ShaderName(urpMat)} color={diagColor} " +
                $"layer={layer}({LayerMask.LayerToName(layer)})");
            return urpMat;
        }

        Shader srtShader = (setLere != null && setLere.matLE != null)
            ? setLere.matLE.shader
            : Shader.Find("Custom/SampleRT");

        if (srtShader == null)
        {
            LogWindow.Error($"AnchorSBS: Custom/SampleRT Shader 未找到，{name} 将使用默认材质");
            return null;
        }

        var diagTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        diagTex.wrapMode = TextureWrapMode.Clamp;
        diagTex.filterMode = FilterMode.Point;
        diagTex.SetPixel(0, 0, diagColor);
        diagTex.Apply();

        var mat = new Material(srtShader) { name = $"{name}_DiagMat" };
        ApplyAnchorSampleRtStereoParams(mat, isLE);
        mat.SetTexture("_mainRT", diagTex);

        meshRenderer.material = mat;

        var mpb = new MaterialPropertyBlock();
        mpb.SetTexture("_mainRT", diagTex);
        mpb.SetInt("_isLE", isLE ? 1 : 0);
        mpb.SetFloat("_visibleRatio", mat.GetFloat("_visibleRatio"));
        mpb.SetFloat("_contentRatio", mat.GetFloat("_contentRatio"));
        mpb.SetFloat("_heightCompressionFactor", mat.GetFloat("_heightCompressionFactor"));
        meshRenderer.SetPropertyBlock(mpb);

        LogWindow.Info(
            $"AnchorSBS [诊断]: {name} path=SampleRT fallback shader=Custom/SampleRT _mainRT={diagTex.width}x{diagTex.height}" +
            $" color={diagColor} _isLE={(isLE ? 1 : 0)} vr={mat.GetFloat("_visibleRatio"):F3} cr={mat.GetFloat("_contentRatio"):F3} " +
            $"hcf={mat.GetFloat("_heightCompressionFactor"):F3} layer={layer}({LayerMask.LayerToName(layer)})");
        return mat;
    }

    /// <summary>
    /// [诊断期间已废弃] 材质设置现在内嵌在 CreateAnchorDiagQuad 中，此方法暂留存根。
    /// </summary>
    private void SetupAnchorSbsMaterials(RawImage leImage, RawImage reImage) { }

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
            ? $"{remoteCameraWindowComp.Texture.width}x{remoteCameraWindowComp.Texture.height}" : "null";
        string rawTex = (anchorSbsRawImage != null && anchorSbsRawImage.texture != null)
            ? $"{anchorSbsRawImage.texture.width}x{anchorSbsRawImage.texture.height}" : "null";
        bool texRef = anchorSbsRawImage != null && remoteCameraWindowComp?.Texture != null
                      && ReferenceEquals(anchorSbsRawImage.texture, remoteCameraWindowComp.Texture);

        LogWindow.Info(
            $"AnchorSBS pipeline[{stage}]: decoderTex={decoderTex}, " +
            $"rawTex={rawTex}(ref={texRef}), " +
            $"mat={ShaderName(anchorSbsLeftHalfMaterial)}, " +
            $"rootActive={(anchorSbsRoot != null && anchorSbsRoot.activeSelf)}");
    }

    private static string ShaderName(Material m) => m != null ? m.shader.name : "null";

    private Camera GetAnchorBillboardCamera()
    {
        if (Camera.main != null && Camera.main.isActiveAndEnabled)
        {
            return Camera.main;
        }

        if (firstCamera != null && firstCamera.isActiveAndEnabled)
        {
            return firstCamera;
        }

        if (secondCamera != null && secondCamera.isActiveAndEnabled)
        {
            return secondCamera;
        }

        return null;
    }

    /// <summary>
    /// 打印所有已知摄像机的 Culling Mask，确认 leLayer/reLayer 是否被各摄像机渲染。
    /// 日志格式：[相机名] mask=0xXXXXXXXX  leLayerBit=0or1  reLayerBit=0or1
    /// bit=1 表示该层在该摄像机的 Culling Mask 中（会被渲染）。
    /// </summary>
    private void LogCameraCullingMask(int leLayer, int reLayer)
    {
        void LogCam(Camera cam, string label)
        {
            if (cam == null) return;
            int mask  = cam.cullingMask;
            int leBit = leLayer >= 0 ? (mask >> leLayer) & 1 : -1;
            int reBit = reLayer >= 0 ? (mask >> reLayer) & 1 : -1;
            LogWindow.Info(
                $"CullMask [{label}] mask=0x{mask:X8}  " +
                $"leLayer({leLayer})={leBit}  reLayer({reLayer})={reBit}");
        }
        LogCam(Camera.main,   "main");
        LogCam(firstCamera,   "firstCam");
        LogCam(secondCamera,  "secondCam");
    }
}
