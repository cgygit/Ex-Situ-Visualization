using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SpatialTracking;
using UnityEngine.InputSystem;
using TMPro;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit;
using System.IO;
using System;

[RequireComponent(typeof(Camera))]
public class PBM_Observer : MonoBehaviour
{
    public static PBM_Observer Instance = null;
    [Header("Default Setting\nAccess individual settings with the dictionary \"PBMs\".")]
    public bool Cropping;
    [Range(0, 1)]
    public float CropSize = 0.5f;
    [Range(0, 1)]
    public float Transparency = 1;
    [Range(0, 0.5f)]
    public float BorderSize = 0.01f;

    [Space]
    public Texture2D BorderTexture;
    public Texture2D MirrorSpecular;

    private Camera ObserverCam;
    private PBM_CaptureCamera CapturingCamera;
    private PBM pbm;

    // PBM variables
    public class PBM
    {
        public Camera SourceCamera;
        public GameObject ImageQuad;
        public Mesh ImageQuadMesh;
        public Material ImageMat;
        public MeshRenderer ImageRenderer;
        public RenderTexture Texture;
        [Header("Cropping and Transparency")]
        public Material CropAndTransparency;
        public bool EnableCropping = true;
        public float CropSize = 0.5f;
        [Range(0, 1)]
        public float Transparency = 1;
        [Range(0, 0.5f)]
        public float BorderSize = 0.002f;
        public PBM()
        {
            CropAndTransparency = new Material(Shader.Find("PBM/CropTransparent"));
        }
        public void DestroyContent()
        {
            Destroy(ImageQuad);
            Destroy(ImageQuadMesh);
            Destroy(ImageMat);
            Destroy(ImageRenderer);
            Destroy(Texture);
        }
    }


    [Header("Calibration")]
    public GameObject Phantom;
    public GameObject SpinePlaceholder;
    public GameObject KinectPlaceholder;
    public GameObject Marker1;
    public GameObject Marker2;
    public GameObject Marker3;
    public GameObject TipCylinder;

    [Header("Input Actions")]
    public InputAction CalibrationSpineAction;  // Left bumper
    public InputAction CalibrationKinectAction; // Right bumper
    public InputAction Round0Action; // Y
    public InputAction Round1Action; // X
    public InputAction Round2Action; // B
    public InputAction Round3Action; // A
    public InputAction PlaceNeedleAction; // Left trigger

    [Header("Colliding Test")]
    public List<GameObject> Cylinders = new List<GameObject>();
    public Collider Gorilla;
    public List<GameObject> SpineCubes = new List<GameObject>();

    private Matrix4x4 OToMarker1;
    private Matrix4x4 OToMarker2;
    private Matrix4x4 OToKinect;

    private bool trackingSpine = false;
    private bool trackingKinect = false;

    private int round = 0;

    // Eye gaze tracking
    private IMixedRealityGazeProvider gazeProvider;
    private string gazeDataFilePath;
    

    private void Awake()
    {
        Instance = this;

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

        ObserverCam = GetComponent<Camera>();

        if (BorderTexture == null)
            BorderTexture = Resources.Load("PBM/PBM_MirrorFrame") as Texture2D;
        if (MirrorSpecular == null)
            MirrorSpecular = Resources.Load("PBM/PBM_MirrorSpecular") as Texture2D;

        gazeProvider = CoreServices.InputSystem.GazeProvider;

        UpdateCylinderVisibility();

        //pbm = new PBM();
        //CapturingCamera = FindObjectOfType<PBM_CaptureCamera>();
        //CapturingCamera.transform.SetPositionAndRotation(OToKinect.GetPosition(), OToKinect.rotation);
        //RegisterCapturer(CapturingCamera);
    }
    

    private void OnEnable()
    {
        CalibrationSpineAction.Enable();
        CalibrationSpineAction.performed += OnCalibrateSpine;

        CalibrationKinectAction.Enable();
        CalibrationKinectAction.performed += OnCalibrateKinect;

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

            if (pbm == null && CapturingCamera == null)
            {
                // First calibration done, initialize pbm
                pbm = new PBM();
                CapturingCamera = FindObjectOfType<PBM_CaptureCamera>();
                CapturingCamera.transform.SetPositionAndRotation(OToKinect.GetPosition(), OToKinect.rotation);
                RegisterCapturer(CapturingCamera);
            }
            else
            {
                // Update kinect transform
                CapturingCamera.transform.SetPositionAndRotation(OToKinect.GetPosition(), OToKinect.rotation);
                pbm.ImageQuad.transform.parent = CapturingCamera.transform;
            }

            
        }
    }

    private void RegisterCapturer(PBM_CaptureCamera capturer)
    {
        pbm.SourceCamera = capturer.GetComponent<Camera>();
        pbm.ImageQuad = new GameObject();
        pbm.ImageQuad.name = "PBM_" + capturer.name;
        pbm.ImageQuad.transform.parent = capturer.transform;
        pbm.ImageQuad.transform.localPosition = Vector3.zero;
        pbm.ImageQuad.transform.localRotation = Quaternion.identity;
        pbm.ImageQuad.transform.localScale = Vector3.one;
        pbm.ImageQuadMesh = new Mesh();
        pbm.ImageQuad.AddComponent<MeshFilter>().mesh = pbm.ImageQuadMesh;

        pbm.ImageMat = Instantiate(Resources.Load("PBM/PBMQuadMaterial") as Material);
        pbm.Texture = new RenderTexture(capturer.Width, capturer.Height, 24, RenderTextureFormat.ARGB32); //new Texture2D(capturer.Width, capturer.Height, TextureFormat.RGBA32, false);
        pbm.ImageMat.mainTexture = pbm.Texture;
        pbm.ImageRenderer = pbm.ImageQuad.AddComponent<MeshRenderer>();
        pbm.ImageRenderer.material = pbm.ImageMat;
        pbm.ImageQuad.layer = LayerMask.NameToLayer("PBM");

        pbm.EnableCropping = Cropping;
        pbm.CropSize = CropSize;
        pbm.Transparency = Transparency;
        pbm.BorderSize = 0.01f;
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
            OToKinect = Matrix4x4.TRS(KinectPlaceholder.transform.position, KinectPlaceholder.transform.rotation, Vector3.one);

            yield return null;
        }

        Marker2.SetActive(false);
    }

    private void Update()
    {
        if (pbm != null && CapturingCamera != null)
        {
            UpdatePBM();
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

    private void UpdatePBM()
    {
        var c_PBM = pbm;

        if (!CapturingCamera.isActiveAndEnabled)
        {
            c_PBM.ImageQuad.SetActive(false);
            return;
        }

        var cameraMidPoint = (CapturingCamera.transform.position + ObserverCam.transform.position) / 2;

        var mirrorNormal = Vector3.Normalize(ObserverCam.transform.position - cameraMidPoint);

        CapturingCamera.UpdateValidAreaCompensationWithObserver(ObserverCam.transform.position);

        if (ComputePlaneCornerIntersection(CapturingCamera, cameraMidPoint, mirrorNormal,
            out var lt_world, out var rt_world, out var rb_world, out var lb_world, true))
        {

            if (Line3DIntersection(lt_world, rb_world, rt_world, lb_world, out var center))
            {

                c_PBM.ImageQuad.SetActive(true);

                c_PBM.CropAndTransparency.SetFloat("CompensationRatio", CapturingCamera.Ratio);
                // Cropping
                if (c_PBM.EnableCropping)
                {
                    c_PBM.CropAndTransparency.SetFloat("_EnableCropping", 1);

                    var gazeRay = new Ray(ObserverCam.transform.position, ObserverCam.transform.forward);
                    Plane p = new Plane(mirrorNormal, cameraMidPoint);

                    if (p.Raycast(gazeRay, out float hitPlane))
                    {
                        var hitPosition = gazeRay.GetPoint(hitPlane);

                        // Project point onto top edge
                        var screenPoint = (Vector2)c_PBM.SourceCamera.WorldToViewportPoint(hitPosition);
                        var cropAndTransMat = c_PBM.CropAndTransparency;

                        cropAndTransMat.SetVector("uv_topleft", new Vector2(Mathf.Clamp01(screenPoint.x - c_PBM.CropSize), Mathf.Clamp01(screenPoint.y - c_PBM.CropSize)));
                        cropAndTransMat.SetVector("uv_topright", new Vector2(Mathf.Clamp01(screenPoint.x + c_PBM.CropSize), Mathf.Clamp01(screenPoint.y - c_PBM.CropSize)));
                        cropAndTransMat.SetVector("uv_bottomleft", new Vector2(Mathf.Clamp01(screenPoint.x - c_PBM.CropSize), Mathf.Clamp01(screenPoint.y + c_PBM.CropSize)));
                        cropAndTransMat.SetVector("uv_bottomright", new Vector2(Mathf.Clamp01(screenPoint.x + c_PBM.CropSize), Mathf.Clamp01(screenPoint.y + c_PBM.CropSize)));
                    }
                }
                else
                {
                    c_PBM.CropAndTransparency.SetFloat("_EnableCropping", 0);
                }

                c_PBM.CropAndTransparency.EnableKeyword("USE_MIRROR_SPECULAR_");

                c_PBM.CropAndTransparency.SetFloat("MainTextureTransparency", c_PBM.Transparency);

                c_PBM.CropAndTransparency.SetFloat("BorderSize", c_PBM.BorderSize);

                c_PBM.CropAndTransparency.SetTexture("_MirrorFrameTex", BorderTexture);

                c_PBM.CropAndTransparency.SetTexture("_MirrorSpecular", MirrorSpecular);


                Graphics.Blit(CapturingCamera.ViewRenderTexture, c_PBM.Texture, c_PBM.CropAndTransparency);

                var cam2Tranform = CapturingCamera.transform.worldToLocalMatrix;
                c_PBM.ImageQuadMesh.vertices = new Vector3[]
                {
                        cam2Tranform.MultiplyPoint(lt_world),
                        cam2Tranform.MultiplyPoint(rt_world),
                        cam2Tranform.MultiplyPoint(rb_world),
                        cam2Tranform.MultiplyPoint(lb_world)
                };

                float lbd = (Vector3.Distance(lb_world, center) + Vector3.Distance(rt_world, center)) / Vector3.Distance(rt_world, center);
                float rbd = (Vector3.Distance(rb_world, center) + Vector3.Distance(lt_world, center)) / Vector3.Distance(lt_world, center);
                float rtb = (Vector3.Distance(rt_world, center) + Vector3.Distance(lb_world, center)) / Vector3.Distance(lb_world, center);
                float ltb = (Vector3.Distance(lt_world, center) + Vector3.Distance(rb_world, center)) / Vector3.Distance(rb_world, center);

                c_PBM.ImageQuadMesh.SetUVs(0, new Vector3[] { new Vector3(0, ltb, ltb), new Vector3(rtb, rtb, rtb), new Vector3(rbd, 0, rbd), new Vector3(0, 0, lbd) });

                c_PBM.ImageQuadMesh.SetIndices(new int[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }, MeshTopology.Triangles, 0);
                c_PBM.ImageQuadMesh.RecalculateBounds();
            }
            else
            {
                c_PBM.ImageQuad.SetActive(false);
            }

        }
        else
        {
            c_PBM.ImageQuad.SetActive(false);
        }

    }

    // http://paulbourke.net/geometry/pointlineplane/
    public bool Line3DIntersection(Vector3 A1, Vector3 A2,
    Vector3 B1, Vector3 B2, out Vector3 intersection)
    {
        intersection = Vector3.zero;

        Vector3 p13 = A1 - B1;
        Vector3 p43 = B2 - B1;

        if (p43.sqrMagnitude < Mathf.Epsilon)
        {
            return false;
        }
        Vector3 p21 = A2 - A1;
        if (p21.sqrMagnitude < Mathf.Epsilon)
        {
            return false;
        }

        float d1343 = p13.x * p43.x + p13.y * p43.y + p13.z * p43.z;
        float d4321 = p43.x * p21.x + p43.y * p21.y + p43.z * p21.z;
        float d1321 = p13.x * p21.x + p13.y * p21.y + p13.z * p21.z;
        float d4343 = p43.x * p43.x + p43.y * p43.y + p43.z * p43.z;
        float d2121 = p21.x * p21.x + p21.y * p21.y + p21.z * p21.z;

        float denom = d2121 * d4343 - d4321 * d4321;
        if (Mathf.Abs(denom) < Mathf.Epsilon)
        {
            return false;
        }
        float numer = d1343 * d4321 - d1321 * d4343;

        float mua = numer / denom;
        float mub = (d1343 + d4321 * (mua)) / d4343;

        var MA = A1 + mua * p21;
        var MB = B1 + mub * p43;

        intersection = (MA + MB) / 2;

        return true;
    }

    public bool ComputePlaneCornerIntersection(PBM_CaptureCamera capturer, Vector3 planeCenter, Vector3 planeNormal, 
        out Vector3 LT, out Vector3 RT, out Vector3 RB, out Vector3 LB, bool useWorldSpace = false)
    {
        var camPos = capturer.transform.position;
        float halfWidth = capturer.Width / 2;
        float halfHeight = capturer.Height / 2;
        float f = capturer.FocalLength;
        // max vertices
        var tlF = capturer.transform.TransformPoint(new Vector3(-halfWidth / f, halfHeight / f, 1));
        var trF = capturer.transform.TransformPoint(new Vector3(halfWidth / f, halfHeight / f, 1));
        var brF = capturer.transform.TransformPoint(new Vector3(halfWidth / f, -halfHeight / f, 1));
        var blF = capturer.transform.TransformPoint(new Vector3(-halfWidth / f, -halfHeight / f, 1));

        var plane = new Plane(planeNormal, planeCenter);

        var rayLT = new Ray(camPos, tlF - camPos);
        var rayRT = new Ray(camPos, trF - camPos);
        var rayRB = new Ray(camPos, brF - camPos);
        var rayLB = new Ray(camPos, blF - camPos);

        if (plane.Raycast(rayLT, out float hitlt) && plane.Raycast(rayRT, out float hitrt) && plane.Raycast(rayRB, out float hitrb) && plane.Raycast(rayLB, out float hitlb))
        {
            LT = rayLT.GetPoint(hitlt);
            RT = rayRT.GetPoint(hitrt);
            RB = rayRB.GetPoint(hitrb);
            LB = rayLB.GetPoint(hitlb);

            if (!useWorldSpace)
            {
                LT = capturer.transform.InverseTransformPoint(LT);
                RT = capturer.transform.InverseTransformPoint(RT);
                RB = capturer.transform.InverseTransformPoint(RB);
                LB = capturer.transform.InverseTransformPoint(LB);
            }
            return true;
        }
        else
        {
            LT = Vector3.zero;
            RT = Vector3.zero;
            RB = Vector3.zero;
            LB = Vector3.zero;
            return false;
        }

    }

}