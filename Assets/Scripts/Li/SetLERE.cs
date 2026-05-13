using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class SetLERE : MonoBehaviour
{

    public GameObject CanvLE;
    public GameObject CanvRE;
    public RemoteCameraWindow remoteCameraWindow;
    public Material matLE;

    public Material matRE;

    //private float visibleRatio = 0.75f;
    //private float contentRatio = 0.88f;
    private float visibleRatio = 0.555f;
    private float contentRatio = 1.8f;
    private float heightCompressionFactor = 1.333333f; // 4:3 aspect ratio
    private bool stereoSplitActive = true;

    public void UpdateParameters(float visible, float content, float heightCompression)
    {
        // Adjust the ratios based on the height compression factor
        visibleRatio = visible;
        contentRatio = content;
        heightCompressionFactor = heightCompression;

        // Log the updated values
        Debug.Log($"Updated Ratios - visible: {visibleRatio}, content: {contentRatio}, heightCompression: {heightCompressionFactor}");
    }
    
    public void ResetCanvases()
    {
        if (CanvLE != null) CanvLE.SetActive(false);
        if (CanvRE != null) CanvRE.SetActive(false);
    }

    public void SetStereoSplitActive(bool isActive)
    {
        // 由外部显示模式控制器调用：非立体分眼模式下，强制关闭双目画布。
        stereoSplitActive = isActive;
        if (!stereoSplitActive)
        {
            ResetCanvases();
        }
        else
        {
            // 切回立体分眼时重置一次，触发材质参数与纹理重新绑定。
            ResetCanvases();
        }
    }

    void Update()
    {
        if (!stereoSplitActive)
        {
            return;
        }

        if (CanvLE == null || CanvRE == null || remoteCameraWindow == null || matLE == null || matRE == null)
        {
            return;
        }

        if ((!CanvLE.activeSelf) || (!CanvRE.activeSelf))
        {
            CanvLE.SetActive(true);
            CanvRE.SetActive(true);

            // 立体分眼模式下，两只眼睛共用同一张解码纹理，仅通过 _isLE 决定采样半幅。
            matLE.SetTexture("_mainRT", remoteCameraWindow.Texture);
            matRE.SetTexture("_mainRT", remoteCameraWindow.Texture);

            matLE.SetInt("_isLE", 1);
            matRE.SetInt("_isLE", 0);

            matLE.SetFloat("_visibleRatio", visibleRatio);
            matRE.SetFloat("_visibleRatio", visibleRatio);
            matLE.SetFloat("_contentRatio", contentRatio);
            matRE.SetFloat("_contentRatio", contentRatio);
            matLE.SetFloat("_heightCompressionFactor", heightCompressionFactor);
            matRE.SetFloat("_heightCompressionFactor", heightCompressionFactor);
        }

        if (Input.GetKeyDown(KeyCode.Q))
        {
            visibleRatio += 0.005f;
            matLE.SetFloat("_visibleRatio", visibleRatio);
            matRE.SetFloat("_visibleRatio", visibleRatio);
            Debug.Log($"visibleRatio: {visibleRatio} - contentRatio: {contentRatio}");
        }

        if (Input.GetKeyDown(KeyCode.E))
        {
            visibleRatio -= 0.005f;
            matLE.SetFloat("_visibleRatio", visibleRatio);
            matRE.SetFloat("_visibleRatio", visibleRatio);
            Debug.Log($"visibleRatio: {visibleRatio} - contentRatio: {contentRatio}");
        }

        if (Input.GetKeyDown(KeyCode.A))
        {
            contentRatio += 0.005f;
            matLE.SetFloat("_contentRatio", contentRatio);
            matRE.SetFloat("_contentRatio", contentRatio);
            Debug.Log($"visibleRatio: {visibleRatio} - contentRatio: {contentRatio}");
        }

        if (Input.GetKeyDown(KeyCode.D))
        {
            contentRatio -= 0.005f;
            matLE.SetFloat("_contentRatio", contentRatio);
            matRE.SetFloat("_contentRatio", contentRatio);
            Debug.Log($"visibleRatio: {visibleRatio} - contentRatio: {contentRatio}");
        }
    }
}
