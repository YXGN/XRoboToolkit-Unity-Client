using System.Collections;
using UnityEngine;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LitJson;
using Network;
using Robot;
using UnityEngine.UI;


/// <summary>
/// Display window of PC camera
/// Responsible for receiving, decoding, and displaying data
/// </summary>
public class RemoteCameraWindow : MonoBehaviour
{
    public RawImage RemoteCameraImage;
    [Header("Follow Setting")]
    public bool followMainCamera = true;
    public Transform followTarget;

    private TcpListener _tcpListener;
    private TcpClient _client;
    private NetworkStream _stream;
    private Texture2D _texture;
    public Texture2D Texture => _texture;
    private byte[] _imageBuffer;
    private CancellationTokenSource _receiveImageTs = null;
    private Task _imageReceiveTask;

    private int _resolutionWidth = 2160;
    private int _resolutionHeight = 2160 / 2 * 4 / 3;
    private int _videoFps = 60;
    private int _bitrate = 40 * 1024 * 1024;

    public CustomButton listenBtn;

    private void Awake()
    {
        // 仅在跟头模式下同步到相机姿态；锚定模式会关闭该开关。
        if (followMainCamera)
        {
            SyncFollowPose();
        }
    }

    public void StartListen(int width, int height, int fps, int bitrate, int port)
    {
        _resolutionWidth = width;
        _resolutionHeight = height;
        _videoFps = fps;
        _bitrate = bitrate;

        StartCoroutine(OnStartListen(port));
    }

    private void OnDisable()
    {
        MediaDecoder.release();
        Debug.Log("RemoteCameraWindow OnDisable");
        TcpHandler.SendFunctionValue("StopReceivePcCamera", "");
    }

    public void OnCloseBtn()
    {
        // Reset listen button
        listenBtn.SetOn(false);
        // send close event to server
        NetworkCommander.Instance.CloseCamera();
        gameObject.SetActive(false);
    }

    public IEnumerator OnStartListen(int port)
    {
        Debug.Log("StartListen port:" + port);

        _texture = new Texture2D(_resolutionWidth, _resolutionHeight, TextureFormat.RGB24, false, false);
        RemoteCameraImage.texture = _texture;
        yield return null;

        MediaDecoder.initialize((int)_texture.GetNativeTexturePtr(), _resolutionWidth, _resolutionHeight);
        MediaDecoder.startServer(port, false);
        yield return null;

        JsonData cameraParam = new JsonData();
        cameraParam["ip"] = Utils.GetLocalIPv4();
        cameraParam["port"] = port;
        cameraParam["width"] = _resolutionWidth;
        cameraParam["height"] = _resolutionHeight;
        cameraParam["fps"] = _videoFps;
        cameraParam["bitrate"] = _bitrate;
        TcpHandler.SendFunctionValue("StartReceivePcCamera", cameraParam.ToJson());
    }

    private void LateUpdate()
    {
        // 跟头模式：每帧跟随相机；空间锚定模式：保持锚点驱动位置不变。
        if (followMainCamera)
        {
            SyncFollowPose();
        }
    }

    private void Update()
    {
        if (_texture != null)
        {
            if (Application.platform == RuntimePlatform.Android)
            {
                if (MediaDecoder.isUpdateFrame())
                {
                    MediaDecoder.updateTexture();
                    GL.InvalidateState();
                }
            }
        }
    }

    public void SetFollowCamera(bool follow)
    {
        followMainCamera = follow;
        if (followMainCamera)
        {
            SyncFollowPose();
        }
    }

    private void SyncFollowPose()
    {
        Transform target = followTarget;
        if (target == null && Camera.main != null)
        {
            target = Camera.main.transform;
        }

        if (target == null)
        {
            return;
        }

        transform.position = target.position;
        transform.rotation = target.rotation;
    }
}