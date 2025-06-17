using Microsoft.MixedReality.Toolkit;
using Microsoft.MixedReality.Toolkit.Input;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

public class BaselineController : MonoBehaviour
{
    [Header("Tracking")]
    public GameObject Phantom;
    public GameObject Marker1;
    public GameObject SpinePlaceholder;
    public GameObject Marker3;
    public GameObject TipCylinder;

    [Header("Input Actions")]
    public InputAction CalibrationSpineAction;  // Left bumper
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
    private bool hasCalibrationDone = false;
    private bool trackingSpine = false;
    private int round = 0;

    // Eye gaze tracking
    private IMixedRealityGazeProvider gazeProvider;
    private string gazeDataFilePath;

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

        gazeProvider = CoreServices.InputSystem.GazeProvider;

        UpdateCylinderVisibility();
    }

    private void OnEnable()
    {
        CalibrationSpineAction.Enable();
        CalibrationSpineAction.performed += OnCalibrateSpine;

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
            hasCalibrationDone = true;

            // Save marker1 position and rotation in csv file
            string logEntryMarker1Position = $",,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},Marker1,{OToMarker1.GetPosition().x},{OToMarker1.GetPosition().y},{OToMarker1.GetPosition().z}\n";
            string logEntryMarker1Rotation = $",,{DateTime.Now.ToString("yyyyMMdd_HHmmss")},Marker1,{OToMarker1.rotation.eulerAngles.x},{OToMarker1.rotation.eulerAngles.y},{OToMarker1.rotation.eulerAngles.z}\n";
            File.AppendAllText(gazeDataFilePath, logEntryMarker1Position);
            File.AppendAllText(gazeDataFilePath, logEntryMarker1Rotation);
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

    private void Update()
    {
        if (hasCalibrationDone)
        {
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
}
