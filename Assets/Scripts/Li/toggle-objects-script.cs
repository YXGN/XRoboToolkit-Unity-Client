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
    [SerializeField] private float anchorDistanceFromHead = 2.5f;
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
    // AnchorSBS 正式图传：每帧通过 MPB 更新纹理，绕过 non-Properties-block 纹理丢失问题
    private MeshRenderer anchorSbsLeRenderer;
    private MeshRenderer anchorSbsReRenderer;
    private MaterialPropertyBlock anchorSbsLeMpb;
    private MaterialPropertyBlock anchorSbsReMpb;
    private Texture anchorSbsLastSyncedTexture;
    // URP Unlit 用 _BaseMap；Custom/SampleRT fallback 用 _mainRT
    private string anchorSbsTexPropName = "_BaseMap";

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
        bool useStereoSplit = mode == DisplayMode.StereoSplit;
        bool useHeadLockedSbs = mode == DisplayMode.HeadLockedSBS;
        bool useAnchorSbs = mode == DisplayMode.AnchorSBS;

        // 每次切换到 AnchorSBS 都基于当前头部位置重新创建锚点与画布。
        // TryCreateAnchorSbsRoot 内部会先销毁旧对象再重建，无需在外部判断 null。
        if (useAnchorSbs)
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

        anchorSbsPipelineBoundLogged = false;

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
    }

    private void SyncSbsTexture()
    {
        if (remoteCameraWindowComp == null || remoteCameraWindowComp.Texture == null) return;
        var tex = remoteCameraWindowComp.Texture;

        // ── 跟头 SBS ─────────────────────────────────────────────────────────
        if (headLockedSbsRawImage != null && !ReferenceEquals(headLockedSbsRawImage.texture, tex))
            headLockedSbsRawImage.texture = tex;

        // ── AnchorSBS 图传纹理同步：直接赋给 RawImage.texture，uvRect 保持不变 ──
        if (currentMode == DisplayMode.AnchorSBS &&
            anchorSbsRawImage != null && anchorSbsRawImageRE != null)
        {
            if (!ReferenceEquals(anchorSbsLastSyncedTexture, tex))
            {
                anchorSbsLastSyncedTexture  = tex;
                anchorSbsRawImage.texture   = tex;
                anchorSbsRawImageRE.texture = tex;
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
            anchorSbsLeRenderer = null;
            anchorSbsReRenderer = null;
            anchorSbsLeMpb = null;
            anchorSbsReMpb = null;
            anchorSbsLastSyncedTexture = null;
            anchorSbsTexPropName = "_BaseMap";
        }

        if (anchorTransform == null || headLockedSbsRoot == null)
        {
            return;
        }

        CreateAnchorPlaceholderFrame(anchorTransform);
    }

    /// <summary>
    /// 在锚点处创建两个 World Space Canvas + RawImage，对应左右眼各一块：
    /// - LE：leLayer 图层，uvRect=(0,   0, 0.5, 1) → 采样 SBS 左半幅
    /// - RE：reLayer 图层，uvRect=(0.5, 0, 0.5, 1) → 采样 SBS 右半幅
    /// 通过 Canvas 渲染路径（UGUI）完全绕开 URP MeshRenderer + CBUFFER tiling/offset 问题。
    /// </summary>
    private void CreateAnchorPlaceholderFrame(Transform parentAnchor)
    {
        if (parentAnchor == null) return;

        const float frameScale = 2f;
        float width  = anchorFrameHeight * (1080f / 720f) * frameScale;
        float height = anchorFrameHeight * frameScale;

        int leLayer = (setLere != null && setLere.CanvLE != null) ? setLere.CanvLE.layer : 0;
        int reLayer = (setLere != null && setLere.CanvRE != null) ? setLere.CanvRE.layer : 0;

        // 父容器：独立场景根节点，Update() 跟随锚点位置
        anchorSbsRoot = new GameObject("AnchorSBS_Root");
        anchorSbsRoot.transform.SetPositionAndRotation(parentAnchor.position, parentAnchor.rotation);
        anchorSbsRoot.transform.localScale = Vector3.one;

        // LE: SBS 左半幅，width=-0.5 补偿 Billboard LookRotation 导致的 Canvas 本地 X 轴镜像
        anchorSbsRawImage = CreateAnchorWorldSpaceRawImage(
            "AnchorSBS_LE", anchorSbsRoot.transform, leLayer, width, height,
            new Rect(0.5f, 0f, -0.5f, 1f));

        // RE: SBS 右半幅
        anchorSbsRawImageRE = CreateAnchorWorldSpaceRawImage(
            "AnchorSBS_RE", anchorSbsRoot.transform, reLayer, width, height,
            new Rect(1f, 0f, -0.5f, 1f));

        // 清空 MeshRenderer/MPB 路径字段（本路径不再使用）
        anchorSbsLeRenderer        = null;
        anchorSbsReRenderer        = null;
        anchorSbsLeMpb             = null;
        anchorSbsReMpb             = null;
        anchorSbsLeftHalfMaterial  = null;
        anchorSbsRightHalfMaterial = null;
        anchorSbsLastSyncedTexture = null;

    }

    /// <summary>
    /// 创建一个 World Space Canvas + RawImage，供 AnchorSBS 左/右眼各用一块。
    /// uvRect 决定采样范围：LE=(0,0,0.5,1)，RE=(0.5,0,0.5,1)。
    /// Canvas 渲染走 UGUI 路径，不受 URP MeshRenderer CBUFFER 限制。
    /// </summary>
    private RawImage CreateAnchorWorldSpaceRawImage(
        string objName, Transform parent, int layer,
        float worldWidth, float worldHeight, Rect uvRect)
    {
        // Canvas（World Space）
        var canvasObj = new GameObject(objName);
        canvasObj.layer = layer;
        canvasObj.transform.SetParent(parent, false);
        canvasObj.transform.localPosition = Vector3.zero;
        canvasObj.transform.localRotation = Quaternion.identity;
        canvasObj.transform.localScale    = Vector3.one;

        var canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.WorldSpace;
        canvas.sortingOrder = anchorSbsCanvasSortingOrder;

        // sizeDelta 直接以世界单位（米）设定画布尺寸（World Space 下 scale=1 时 1px = 1unit）
        var rt = canvasObj.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(worldWidth, worldHeight);

        // 全铺 RawImage 子对象
        var imgObj = new GameObject(objName + "_Img");
        imgObj.layer = layer;
        imgObj.transform.SetParent(canvasObj.transform, false);
        var imgRt         = imgObj.AddComponent<RectTransform>();
        imgRt.anchorMin   = Vector2.zero;
        imgRt.anchorMax   = Vector2.one;
        imgRt.offsetMin   = Vector2.zero;
        imgRt.offsetMax   = Vector2.zero;
        imgRt.pivot       = new Vector2(0.5f, 0.5f);

        var rawImage      = imgObj.AddComponent<RawImage>();
        rawImage.uvRect   = uvRect;
        rawImage.color    = Color.white;

        return rawImage;
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
        LogWindow.Info($"AnchorSBS: 锚点 pos={anchorPos.ToString("F3")}");
        // 使用 Assembly-CSharp 内的宿主脚本写 LogWindow；SDK 内 PXR_SpatialAnchor 无法引用 LogWindow。
        runtimeAnchorObj.AddComponent<SpatialAnchorRuntimeHost>();

        anchorTransform = runtimeAnchorTransform;
        return true;
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
