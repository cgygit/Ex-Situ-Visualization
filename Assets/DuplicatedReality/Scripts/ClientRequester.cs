using UnityEngine;
using TMPro; // Import the TextMeshPro namespace
using System;
using NetMQ;
using UnityMainThreadDispatcher;
using UnityEngine.UI;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;   // For List<>
using PubSub;
using NetMQ.Sockets;
using System.Collections;
using UnityEngine.Timeline;
using UnityEngine.SpatialTracking;
using UnityEngine.InputSystem;
using static PBM_Observer;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit;
using DuplicatedReality;
using System.IO;

namespace Kinect4Azure
{
    public class ClientRequester : MonoBehaviour
    {
        [Header("Duplicated Reality")]
        public Transform RegionOfInterest;
        public Transform DuplicatedReality;

        [Header("Networking")]
        [SerializeField] private string host;
        [SerializeField] private string port = "12345";
        private RequestSocket requestSocket;

        [Serializable]
        public struct PointcloudShader
        {
            public string ID;
            public string ShaderName;
        }
        [Header("Pointcloud Configs")]
        public ComputeShader Depth2BufferShader;
        public List<PointcloudShader> Shaders;
        private int _CurrentSelectedShader = 0;
        private Material _Buffer2SurfaceMaterial;

        [Range(0.01f, 0.1f)]
        public float MaxPointDistance = 0.02f;

        [Header("Background Configs\n(Only works if this script is attached onto the camera)")]
        public bool EnableARBackground = true;
        [Tooltip("Only needs to be set when BlitToCamera is checked")]
        public Material ARBackgroundMaterial;

        [Header("ReadOnly and exposed for Debugging: Initial Message")]
        [SerializeField] private int ColorWidth;
        [SerializeField] private int ColorHeight;
        [SerializeField] private int DepthWidth;
        [SerializeField] private int DepthHeight;
        [SerializeField] private int IRWidth;
        [SerializeField] private int IRHeight;
        [SerializeField] private Texture2D XYLookup;
        [SerializeField] private Matrix4x4 Color2DepthCalibration;
        [SerializeField] private int kernel;
        [SerializeField] private int dispatch_x;
        [SerializeField] private int dispatch_y;
        [SerializeField] private int dispatch_z;

        [Header("ReadOnly and exposed for Debugging: Update for every Frame")]
        [SerializeField] private Texture2D DepthImage;
        [SerializeField] private Texture2D ColorInDepthImage;

        [Header("Calibration")]
        public GameObject Phantom;
        public GameObject Kinect;
        public GameObject SpinePlaceholder;
        public GameObject KinectPlaceholder;
        public GameObject Marker1;
        public GameObject Marker2;
        public GameObject Marker3;
        public GameObject TipCylinder;

        [Header("Input Actions")]
        public InputAction CalibrationSpineAction;  // Left bumper
        public InputAction CalibrationKinectAction; // Right bumper
        public InputAction SwitchToNextShaderAction; // Right trigger
        public InputAction Round0Action; // Y
        public InputAction Round1Action; // X
        public InputAction Round2Action; // B
        public InputAction Round3Action; // A
        public InputAction PlaceNeedleAction; // Left trigger

        [Header("Colliding Test")]
        public List<GameObject> Cylinders = new List<GameObject>();
        public Collider Gorilla;
        public List<GameObject> SpineCubes = new List<GameObject>();

        private byte[] cameraData;
        private byte[] xyLookupDataPart1;
        private byte[] xyLookupDataPart2;
        private byte[] xyLookupDataPart3;

        private byte[] depthData;
        private byte[] colorInDepthData;
        private static readonly object dataLock = new object();

        private bool hasReceivedCamera = false;
        private bool hasReceivedLookup1 = false;
        private bool hasReceivedLookup2 = false;
        private bool hasReceivedLookup3 = false;

        // Buffers for PointCloud Compute Shader
        private Vector3[] vertexBuffer;
        private Vector2[] uvBuffer;
        private int[] indexBuffer;
        private ComputeBuffer _ib;
        private ComputeBuffer _ub;
        private ComputeBuffer _vb;

        private Matrix4x4 OToMarker1;
        private Matrix4x4 OToMarker2;

        private bool trackingSpine = false;
        private bool trackingKinect = false;

        private int round = 0;

        // Eye gaze tracking
        private IMixedRealityGazeProvider gazeProvider;
        private string gazeDataFilePath;

        private string delayLogFilePath;


        private void Awake()
        {
            // Create csv file
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            gazeDataFilePath = Path.Combine(Application.persistentDataPath, $"GazeHitData_{timestamp}.csv");
            Debug.Log($"Save file at {gazeDataFilePath}");

            if (!File.Exists(gazeDataFilePath))
            {
                // Type: 
                // 0: HeadPosition; 1: HeadForward; 2: HeadUp; 3: HeadRight
                // 4: GazeOrigin; 5: GazeDirection; 6: GazeHitPhantom; 7: GazeHitSpineCubes; 8: GazeHitCylinders; 9: GazeHitGorilla
                File.AppendAllText(gazeDataFilePath, "Round,Type,Timestamp,HitObject,PositionX,PositionY,PositionZ\n");
            }

            // Create csv file for delay
            delayLogFilePath = Path.Combine(Application.persistentDataPath, "DelayLog.csv");
            if (!File.Exists(delayLogFilePath))
            {
                File.AppendAllText(delayLogFilePath, "Timestamp,DelayMilliseconds\n");
            }

            gazeProvider = CoreServices.InputSystem.GazeProvider;

            UpdateCylinderVisibility();
        }

        private void OnEnable()
        {
            CalibrationSpineAction.Enable();
            CalibrationSpineAction.performed += OnCalibrateSpine;

            CalibrationKinectAction.Enable();
            CalibrationKinectAction.performed += OnCalibrateKinect;

            SwitchToNextShaderAction.Enable();
            SwitchToNextShaderAction.performed += OnSwitchToNextShader;

            Round0Action.Enable();
            Round0Action.performed += OnRound0;

            Round1Action.Enable();
            Round1Action.performed += OnRound1;

            Round2Action.Enable();
            Round2Action.performed += OnRound2;

            Round3Action.Enable();
            Round3Action.performed += OnRound3;

            PlaceNeedleAction.Enable();
            PlaceNeedleAction.performed += OnPlaceNeedle;
        }

        private void OnDisable()
        {
            CalibrationSpineAction.Disable();
            CalibrationSpineAction.performed -= OnCalibrateSpine;

            CalibrationKinectAction.Disable();
            CalibrationKinectAction.performed -= OnCalibrateKinect;

            SwitchToNextShaderAction.Disable();
            SwitchToNextShaderAction.performed -= OnSwitchToNextShader;

            Round0Action.Disable();
            Round0Action.performed -= OnRound0;

            Round1Action.Disable();
            Round1Action.performed -= OnRound1;

            Round2Action.Disable();
            Round2Action.performed -= OnRound2;

            Round3Action.Disable();
            Round3Action.performed -= OnRound3;

            PlaceNeedleAction.Disable();
            PlaceNeedleAction.performed -= OnPlaceNeedle;
        }

        private void OnRound0(InputAction.CallbackContext context)
        {
            round = 0;
            UpdateCylinderVisibility();
        }

        private void OnRound1(InputAction.CallbackContext context)
        {
            round = 1;
            UpdateCylinderVisibility();
        }

        private void OnRound2(InputAction.CallbackContext context)
        {
            round = 2;
            UpdateCylinderVisibility();
        }

        private void OnRound3(InputAction.CallbackContext context)
        {
            round = 3;
            UpdateCylinderVisibility();
        }

        private void OnPlaceNeedle(InputAction.CallbackContext context)
        {
            // Record TipCylinder
            Vector3 tipPos = TipCylinder.transform.position;
            Quaternion tipRot = TipCylinder.transform.rotation;

            string tipLogPos = $"{round},10,{DateTime.Now:yyyyMMdd_HHmmss},TipPosition,{tipPos.x},{tipPos.y},{tipPos.z}\n";
            string tipLogRot = $"{round},11,{DateTime.Now:yyyyMMdd_HHmmss},TipRotation,{tipRot.eulerAngles.x},{tipRot.eulerAngles.y},{tipRot.eulerAngles.z}\n";
            File.AppendAllText(gazeDataFilePath, tipLogPos);
            File.AppendAllText(gazeDataFilePath, tipLogRot);

            // Find which active cylinder
            Collider tipCollider = TipCylinder.GetComponent<Collider>();
            foreach (var cylinder in Cylinders)
            {
                if (!cylinder.activeSelf) continue;

                Collider cylCollider = cylinder.GetComponent<Collider>();
                if (tipCollider.bounds.Intersects(cylCollider.bounds))
                {
                    Vector3 cylPos = cylinder.transform.position;
                    Quaternion cylRot = cylinder.transform.rotation;

                    // Record Cylinder
                    string cylLogPos = $"{round},12,{DateTime.Now:yyyyMMdd_HHmmss},{cylinder.name}_Position,{cylPos.x},{cylPos.y},{cylPos.z}\n";
                    string cylLogRot = $"{round},13,{DateTime.Now:yyyyMMdd_HHmmss},{cylinder.name}_Rotation,{cylRot.eulerAngles.x},{cylRot.eulerAngles.y},{cylRot.eulerAngles.z}\n";
                    File.AppendAllText(gazeDataFilePath, cylLogPos);
                    File.AppendAllText(gazeDataFilePath, cylLogRot);

                    // Disable Cylinder
                    cylinder.SetActive(false);

                    Debug.Log($"TipCylinder collided with {cylinder.name}");
                }
            }
        }

        private void UpdateCylinderVisibility()
        {
            for (int i = 0; i < Cylinders.Count; i++)
            {
                if (round == 0)
                {
                    Cylinders[i].SetActive(false); // all not visible
                }
                else if (round == 1 && i < 2)
                {
                    Cylinders[i].SetActive(true); // only first two visible
                }
                else if (round == 2 && i < 4)
                {
                    Cylinders[i].SetActive(true); // only first four visible
                }
                else if (round == 3 && i >= 4)
                {
                    Cylinders[i].SetActive(true); // only last four visible
                }
                else
                {
                    Cylinders[i].SetActive(false);
                }
            }

            Debug.Log($"Round: {round}, Cylinder Visibility Updated.");
        }

        private void OnSwitchToNextShader(InputAction.CallbackContext context)
        {
            NextShaderInList();
        }

        [ContextMenu("Next Shader")]
        public void NextShaderInList()
        {
            int nextShaderIndex = (_CurrentSelectedShader + 1) % Shaders.Count;
            SwitchPointCloudShader(nextShaderIndex);
        }

        public bool SwitchPointCloudShader(int indexInList)
        {
            Debug.Log("KinectSubscriber::SwitchPointCloudShader(int indexInList) " + indexInList);
            var currentShaderName = Shaders[indexInList].ShaderName;

            var pc_shader = Shader.Find(currentShaderName);
            if (!pc_shader)
            {
                Debug.LogError("KinectSubscriber::SwitchPointCloudShader(): " + currentShaderName + " shader not found");
                return false;
            }
            _CurrentSelectedShader = indexInList;

            if (!_Buffer2SurfaceMaterial) _Buffer2SurfaceMaterial = new Material(pc_shader);
            else _Buffer2SurfaceMaterial.shader = pc_shader;

            _Buffer2SurfaceMaterial.SetBuffer("vertices", _vb);
            _Buffer2SurfaceMaterial.SetBuffer("uv", _ub);
            _Buffer2SurfaceMaterial.SetBuffer("triangles", _ib);
            _Buffer2SurfaceMaterial.mainTexture = ColorInDepthImage;

            return true;
        }

        private void OnCalibrateSpine(InputAction.CallbackContext context)
        {
            if (!trackingSpine)
            {
                StartCoroutine(CalibrateSpine());
            }
            else
            {
                trackingSpine = false;

                // Save marker1 position and rotation in csv file
                string logEntryMarker1Position = $",,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},Marker1,{OToMarker1.GetPosition().x},{OToMarker1.GetPosition().y},{OToMarker1.GetPosition().z}\n";
                string logEntryMarker1Rotation = $",,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},Marker1,{OToMarker1.rotation.eulerAngles.x},{OToMarker1.rotation.eulerAngles.y},{OToMarker1.rotation.eulerAngles.z}\n";
                File.AppendAllText(gazeDataFilePath, logEntryMarker1Position);
                File.AppendAllText(gazeDataFilePath, logEntryMarker1Rotation);
            }
        }

        private void OnCalibrateKinect(InputAction.CallbackContext context)
        {
            if (!trackingKinect)
            {
                StartCoroutine(CalibrateKinect());
            }
            else
            {
                trackingKinect = false;

                // Save marker2 position and rotation in csv file
                string logEntryMarker2Position = $",,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},Marker2,{OToMarker2.GetPosition().x},{OToMarker2.GetPosition().y},{OToMarker2.GetPosition().z}\n";
                string logEntryMarker2Rotation = $",,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},Marker2,{OToMarker2.rotation.eulerAngles.x},{OToMarker2.rotation.eulerAngles.y},{OToMarker2.rotation.eulerAngles.z}\n";
                File.AppendAllText(gazeDataFilePath, logEntryMarker2Position);
                File.AppendAllText(gazeDataFilePath, logEntryMarker2Rotation);

                if (requestSocket == null)
                {
                    // First calibration done, initialize socket
                    InitializeSocket();
                    StartCoroutine(RequestDataLoop());
                }
            }
        }

        private IEnumerator CalibrateSpine()
        {
            trackingSpine = true;
            Marker1.SetActive(true);

            while (trackingSpine)
            {
                OToMarker1 = Matrix4x4.TRS(Marker1.transform.position, Marker1.transform.rotation, Vector3.one);
                Phantom.transform.SetPositionAndRotation(SpinePlaceholder.transform.position, SpinePlaceholder.transform.rotation);

                yield return null;
            }

            Marker1.SetActive(false);
        }

        private IEnumerator CalibrateKinect()
        {
            trackingKinect = true;
            Marker2.SetActive(true);

            while (trackingKinect)
            {
                OToMarker2 = Matrix4x4.TRS(Marker2.transform.position, Marker2.transform.rotation, Vector3.one);
                Kinect.transform.SetPositionAndRotation(KinectPlaceholder.transform.position, KinectPlaceholder.transform.rotation);

                yield return null;
            }

            Marker2.SetActive(false);
        }


        private void InitializeSocket()
        {
            try
            {
                AsyncIO.ForceDotNet.Force();
                requestSocket = new RequestSocket();
                requestSocket.Connect($"tcp://{host}:{port}");
                Debug.Log("Connected to server");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to connect socket: {ex.Message}");
            }
        }

        private IEnumerator RequestDataLoop()
        {

            while (!hasReceivedCamera)
            {
                RequestCameraData();
                yield return new WaitForSeconds(0.2f); // Retry every 200ms
            }

            while (!hasReceivedLookup1)
            {
                RequestLookupData(1);
                yield return new WaitForSeconds(0.2f);
            }

            while (!hasReceivedLookup2)
            {
                RequestLookupData(2);
                yield return new WaitForSeconds(0.2f);
            }

            while (!hasReceivedLookup3)
            {
                RequestLookupData(3);
                yield return new WaitForSeconds(0.2f);
            }

            Debug.Log("All required data received.");
            ProcessInitialData();
            StartCoroutine(RequestFrameDataLoop());
        }

        private void RequestCameraData()
        {
            requestSocket.SendFrame("Camera");
            if (requestSocket.TryReceiveFrameBytes(TimeSpan.FromSeconds(1), out var data))
            {
                Debug.Log($"Received Camera data: {data.Length} bytes");
                cameraData = data;
                hasReceivedCamera = true;
            }
            else
            {
                Debug.LogWarning("Camera data request timed out");
            }
        }

        private void RequestLookupData(int part)
        {
            requestSocket.SendFrame($"Lookup{part}");
            if (requestSocket.TryReceiveFrameBytes(TimeSpan.FromSeconds(1), out var data))
            {
                Debug.Log($"Received Lookup{part} data: {data.Length} bytes");
                if (part == 1)
                { xyLookupDataPart1 = data; hasReceivedLookup1 = true; }
                if (part == 2)
                { xyLookupDataPart2 = data; hasReceivedLookup2 = true; }
                if (part == 3)
                { xyLookupDataPart3 = data; hasReceivedLookup3 = true; }
            }
            else
            {
                Debug.LogWarning($"Lookup{part} data request timed out");
            }
        }

        private void ProcessInitialData()
        {
            /* Process camera data */
            try
            {
                int calibrationDataLength = BitConverter.ToInt32(cameraData, 0);
                int cameraSizeDataLength = BitConverter.ToInt32(cameraData, sizeof(int) * 1);

                byte[] calibrationData = new byte[calibrationDataLength];
                Buffer.BlockCopy(cameraData, sizeof(int) * 2, calibrationData, 0, calibrationDataLength);
                byte[] cameraSizeData = new byte[cameraSizeDataLength];
                Buffer.BlockCopy(cameraData, sizeof(int) * 2 + calibrationDataLength, cameraSizeData, 0, cameraSizeDataLength);

                int[] captureArray = new int[6];
                Buffer.BlockCopy(cameraSizeData, 0, captureArray, 0, cameraSizeData.Length);
                ColorWidth = captureArray[0];
                ColorHeight = captureArray[1];
                DepthWidth = captureArray[2];
                DepthHeight = captureArray[3];
                IRWidth = captureArray[4];
                IRHeight = captureArray[5];

                SetupTextures(ref DepthImage, ref ColorInDepthImage);

                Color2DepthCalibration = ByteArrayToMatrix4x4(calibrationData);
            }
            catch (Exception e)
            {
                Debug.LogError("Error in OnCameraReceived: " + e.Message);
            }

            /* Process lookup data */
            byte[] xyLookupData = new byte[xyLookupDataPart1.Length + xyLookupDataPart2.Length + xyLookupDataPart3.Length];
            System.Buffer.BlockCopy(xyLookupDataPart1, 0, xyLookupData, 0, xyLookupDataPart1.Length);
            System.Buffer.BlockCopy(xyLookupDataPart2, 0, xyLookupData, xyLookupDataPart1.Length, xyLookupDataPart2.Length);
            System.Buffer.BlockCopy(xyLookupDataPart3, 0, xyLookupData, xyLookupDataPart1.Length + xyLookupDataPart2.Length, xyLookupDataPart3.Length);

            XYLookup = new Texture2D(DepthImage.width, DepthImage.height, TextureFormat.RGBAFloat, false);
            XYLookup.LoadRawTextureData(xyLookupData);
            XYLookup.Apply();

            if (!SetupShaders(57 /*Standard Kinect Depth FoV*/, DepthImage.width, DepthImage.height, out kernel))
            {
                Debug.LogError("OnLookupsReceived(): Something went wrong while setting up shaders");
                return;
            }

            // Compute kernel group sizes. If it deviates from 32-32-1, this need to be adjusted inside Depth2Buffer.compute as well.
            Depth2BufferShader.GetKernelThreadGroupSizes(kernel, out var xc, out var yc, out var zc);

            dispatch_x = (DepthImage.width + (int)xc - 1) / (int)xc;
            dispatch_y = (DepthImage.height + (int)yc - 1) / (int)yc;
            dispatch_z = (1 + (int)zc - 1) / (int)zc;
            Debug.Log("OnLookupsReceived(): Kernel group sizes are " + xc + "-" + yc + "-" + zc);
        }

        private IEnumerator RequestFrameDataLoop()
        {
            while (true)
            {
                requestSocket.SendFrame("Frame");

                if (requestSocket.TryReceiveFrameBytes(TimeSpan.FromSeconds(1), out var msg))
                {
                    //Debug.Log($"Received Frame data: {msg.Length} bytes");

                    // Get delay
                    long timestamp = BitConverter.ToInt64(msg, 0);
                    byte[] data = new byte[msg.Length - sizeof(long)];
                    Buffer.BlockCopy(msg, sizeof(long), data, 0, data.Length);

                    long receivedTimestamp = DateTime.UtcNow.Ticks;
                    double delayMilliseconds = (receivedTimestamp - timestamp) / TimeSpan.TicksPerMillisecond;

                    string logLine = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff},{delayMilliseconds:F2}\n";
                    File.AppendAllText(delayLogFilePath, logLine);

                    // Get data
                    lock (dataLock)
                    {
                        int depthDataLength = BitConverter.ToInt32(data, 0);
                        int colorInDepthDataLength = BitConverter.ToInt32(data, sizeof(int));

                        depthData = new byte[depthDataLength];
                        Buffer.BlockCopy(data, sizeof(int) * 2, depthData, 0, depthDataLength);

                        colorInDepthData = new byte[colorInDepthDataLength];
                        Buffer.BlockCopy(data, sizeof(int) * 2 + depthDataLength, colorInDepthData, 0, colorInDepthDataLength);
                    }
                }
                else
                {
                    Debug.LogWarning("Frame data request timed out");
                }

                yield return null; // Frame requests as fast as possible
            }
        }


        private void Update()
        {
            if (depthData != null && colorInDepthData != null)
            {
                long timeBegin = DateTime.UtcNow.Ticks;
                
                lock (dataLock)
                {
                    DepthImage.LoadRawTextureData(depthData.ToArray());
                    DepthImage.Apply();

                    ColorInDepthImage.LoadRawTextureData(colorInDepthData.ToArray());
                    ColorInDepthImage.Apply();
                }

                // Compute triangulation of PointCloud + maybe duplicate depending on the shader
                Depth2BufferShader.SetFloat("_maxPointDistance", MaxPointDistance);
                Depth2BufferShader.SetMatrix("_Transform", Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one));
                Depth2BufferShader.Dispatch(kernel, dispatch_x, dispatch_y, dispatch_z);

                // Draw resulting PointCloud
                int pixel_count = DepthImage.width * DepthImage.height;

                // Set Pointcloud Properties
                _Buffer2SurfaceMaterial.SetMatrix("_Roi2Dupl", DuplicatedReality.localToWorldMatrix * RegionOfInterest.worldToLocalMatrix);
                _Buffer2SurfaceMaterial.SetMatrix("_ROI_Inversed", RegionOfInterest.worldToLocalMatrix);
                _Buffer2SurfaceMaterial.SetMatrix("_Dupl_Inversed", DuplicatedReality.worldToLocalMatrix);

                Graphics.DrawProcedural(_Buffer2SurfaceMaterial, new Bounds(transform.position, Vector3.one * 10), MeshTopology.Triangles, pixel_count * 6);

                long timeEnd = DateTime.UtcNow.Ticks;
                double timeDiff = (timeEnd - timeBegin) / TimeSpan.TicksPerMillisecond;

                // Check gaze hit every frame second after calibration
                CheckGazeHit();
            }
        }

        private void CheckGazeHit()
        {
            if (gazeProvider == null)
                return;

            /* Head */
            Vector3 headPosition = Camera.main.transform.position;
            Vector3 headForward = Camera.main.transform.forward;
            Vector3 headUp = Camera.main.transform.up;
            Vector3 headRight = Camera.main.transform.right;

            string logEntry0 = $"{round},0,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},HeadPosition,{headPosition.x},{headPosition.y},{headPosition.z}\n";
            File.AppendAllText(gazeDataFilePath, logEntry0);
            string logEntry1 = $"{round},1,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},HeadForward,{headForward.x},{headForward.y},{headForward.z}\n";
            File.AppendAllText(gazeDataFilePath, logEntry1);
            string logEntry2 = $"{round},2,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},HeadUp,{headUp.x},{headUp.y},{headUp.z}\n";
            File.AppendAllText(gazeDataFilePath, logEntry2);
            string logEntry3 = $"{round},3,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},HeadRight,{headRight.x},{headRight.y},{headRight.z}\n";
            File.AppendAllText(gazeDataFilePath, logEntry3);

            /* Eye */
            Vector3 gazeOrigin = gazeProvider.GazeOrigin;
            Vector3 gazeDirection = gazeProvider.GazeDirection;
            Ray gazeRay = new Ray(gazeOrigin, gazeDirection);
            RaycastHit hitInfo;

            string logEntry4 = $"{round},4,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},GazeOrigin,{gazeOrigin.x},{gazeOrigin.y},{gazeOrigin.z}\n";
            File.AppendAllText(gazeDataFilePath, logEntry4);
            string logEntry5 = $"{round},5,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},GazeDirection,{gazeDirection.x},{gazeDirection.y},{gazeDirection.z}\n";
            File.AppendAllText(gazeDataFilePath, logEntry5);

            // Only when it hits phantom, then it can hit others
            if (Phantom.GetComponent<Collider>().Raycast(gazeRay, out hitInfo, Mathf.Infinity))
            {
                string logEntry6 = $"{round},6,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},Phantom,{hitInfo.point.x},{hitInfo.point.y},{hitInfo.point.z}\n";
                File.AppendAllText(gazeDataFilePath, logEntry6);
                Debug.Log($"Phantom: {hitInfo}");

                foreach (var cube in SpineCubes)
                {
                    if (cube.GetComponent<Collider>().Raycast(gazeRay, out hitInfo, Mathf.Infinity))
                    {
                        string logEntry7 = $"{round},7,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},{cube.name},{hitInfo.point.x},{hitInfo.point.y},{hitInfo.point.z}\n";
                        File.AppendAllText(gazeDataFilePath, logEntry7);
                    }
                }

                // Only test active cylinders
                foreach (var cylinder in Cylinders)
                {
                    if (cylinder.activeSelf && cylinder.GetComponent<Collider>().Raycast(gazeRay, out hitInfo, Mathf.Infinity))
                    {
                        string logEntry8 = $"{round},8,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},{cylinder.name},{hitInfo.point.x},{hitInfo.point.y},{hitInfo.point.z}\n";
                        File.AppendAllText(gazeDataFilePath, logEntry8);
                    }
                }

                if (Gorilla.GetComponent<Collider>().Raycast(gazeRay, out hitInfo, Mathf.Infinity))
                {
                    string logEntry9 = $"{round},9,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},Gorilla,{hitInfo.point.x},{hitInfo.point.y},{hitInfo.point.z}\n";
                    File.AppendAllText(gazeDataFilePath, logEntry9);
                }
            }
        }

        private void SetupTextures(ref Texture2D Depth, ref Texture2D ColorInDepth)
        {
            Debug.Log("Setting up textures: DepthWidth=" + DepthWidth + " DepthHeight=" + DepthHeight);

            if (Depth == null)
                Depth = new Texture2D(DepthWidth, DepthHeight, TextureFormat.R16, false);
            if (ColorInDepth == null)
                ColorInDepth = new Texture2D(IRWidth, IRHeight, TextureFormat.BGRA32, false);
        }

        private Matrix4x4 ByteArrayToMatrix4x4(byte[] byteArray)
        {
            float[] matrixFloats = new float[16];
            Buffer.BlockCopy(byteArray, 0, matrixFloats, 0, byteArray.Length);

            Matrix4x4 matrix = new Matrix4x4();
            for (int i = 0; i < 16; i++)
            {
                matrix[i] = matrixFloats[i];
            }

            return matrix;
        }

        private bool SetupShaders(float foV, int texWidth, int texHeight, out int kernelID)
        {
            kernelID = 0;
            // Setup Compute Shader
            if (!Depth2BufferShader)
            {
                Debug.LogError("KinectSubscriber::SetupShaders(): Depth2BufferShader compute shader not found");
                return false;
            }

            kernelID = Depth2BufferShader.FindKernel("Compute");

            Depth2BufferShader.SetInt("_DepthWidth", texWidth);
            Depth2BufferShader.SetInt("_DepthHeight", texHeight);

            // apply sensor to device offset
            Depth2BufferShader.SetMatrix("_Col2DepCalibration", Color2DepthCalibration);

            // Setup Depth2Mesh Shader and reading buffers
            int size = texWidth * texHeight;

            vertexBuffer = new Vector3[size];
            uvBuffer = new Vector2[size];
            indexBuffer = new int[size * 6];

            _vb = new ComputeBuffer(vertexBuffer.Length, 3 * sizeof(float));
            _ub = new ComputeBuffer(uvBuffer.Length, 2 * sizeof(float));
            _ib = new ComputeBuffer(indexBuffer.Length, sizeof(int));

            // Set Kernel variables
            Depth2BufferShader.SetBuffer(kernelID, "vertices", _vb);
            Depth2BufferShader.SetBuffer(kernelID, "uv", _ub);
            Depth2BufferShader.SetBuffer(kernelID, "triangles", _ib);

            Depth2BufferShader.SetTexture(kernelID, "_DepthTex", DepthImage);
            Depth2BufferShader.SetTexture(kernelID, "_XYLookup", XYLookup);

            if (Shaders.Count == 0)
            {
                Debug.LogError("KinectSubscriber::SetupShaders(): Provide at least one point cloud shader");
                return false;
            }

            // Setup Rendering Shaders
            SwitchPointCloudShader(_CurrentSelectedShader);

            return true;
        }


        private void ReleaseBuffers()
        {
            _vb?.Dispose();
            _ub?.Dispose();
            _ib?.Dispose();
        }

        private void OnDestroy()
        {
            Debug.Log("Destroying subscriber...");

            if (requestSocket != null)
            {
                requestSocket.Dispose();
                requestSocket = null;
                NetMQConfig.Cleanup(false);
            }

            ReleaseBuffers();
        }
    }
}
