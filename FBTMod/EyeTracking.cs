using BuildSoft.OscCore;
using Il2CppRUMBLE.Managers;
using Il2CppRUMBLE.Players;
using Il2CppRUMBLE.Players.Scaling;
using Il2CppRUMBLE.Utilities;
using Il2CppSteamworks;
using MelonLoader;
using System;
using UnityEngine;
using UnityEngine.Animations;
using static FBTMod.FullBodyTracking;

namespace FBTMod;

[RegisterTypeInIl2Cpp]
public class EyeTracking : MonoBehaviour
{
    public PlayerController Owner;

    public float LeftPitch = 0;
    public float RightPitch = 0;
    public float LeftYaw = 0;
    public float RightYaw = 0;
    public float CloseAmount = 0;

    public FBTType Type = FBTType.None;

    public bool Disabled = true;

    public void Start()
    {
        Main.AllETs.Add(this);
    }

    public void Destroy()
    {
        Main.AllETs.Remove(this);
    }

    public void Update()
    {
        if (!Main.globalInit) return;
        if (Disabled) return;

        if (Type is FBTType.Tracked)
        {
            LeftPitch = Main.LocalLeftPitch;
            LeftYaw = Main.LocalLeftYaw;
            RightPitch = Main.LocalRightPitch;
            RightYaw = Main.LocalRightYaw;
            CloseAmount = Main.LocalCloseAmount;
        }

        Transform head = Owner.PlayerVR.headset.Transform;

        float avgPitch = (LeftPitch + RightPitch) / 2f;
        float avgYaw = (LeftYaw + RightYaw) / 2f;
        Vector3 lookDir = head.rotation * Quaternion.Euler(avgPitch, avgYaw * 1.2f, 0.0f) * Vector3.forward;

        Vector3 lookTarget = Vector3.zero;
        if (Physics.Raycast(head.position + lookDir * 0.4f, lookDir, out RaycastHit hit, 100f))
        {
            lookTarget = hit.point + lookDir * -0.3f;
        }
        else
        {
            lookTarget = head.transform.position + lookDir * 100f;
        }

        Vector2 screenPoint = RecordingCamera.Instance.LegacyCamera.WorldToScreenPoint(lookTarget);

        float dist = Vector3.Distance(head.transform.position, lookTarget);


        if (Type is FBTType.Tracked)
        {
            //if (Main.ReceivedDataThisFrame)
            //    Main.TimeLastDataReceived = DateTime.Now;
            //Main.ReceivedDataThisFrame = false;

            Main.GazeVisualizerMat.SetFloat("_Darkness", 0.7f);
            Main.GazeVisualizerMat.SetFloat("_Proportional", 1f - CloseAmount);
            Main.GazeVisualizerMat.SetFloat("_Radius", 1f / Mathf.Clamp(dist, 2.25f, 60f) / 7.5f);
            Main.GazeVisualizerMat.SetVector("_Center", new Vector4(screenPoint.x / Screen.width, screenPoint.y / Screen.height, 0f, 0f));
        }
    }

    public void LateUpdate()
    {
        if (Disabled) return;

        if (Type is FBTType.Tracked && Config.EnableEyeTracking.Value)
        {
            UpdateBones();
        }
        if (Type is FBTType.Networked)
        {
            // APPLY NETWORKING
            UpdateBones();
        }
        if (Type is FBTType.Animated)
        {
            // Animation is applied by ETReplayExtension
            UpdateBones();
        }
    }

    public Quaternion GetLeftEyeRot()
    {
        return Quaternion.Euler((RightPitch + LeftPitch) / 2f, LeftYaw, 0.0f);
    }

    public Quaternion GetRightEyeRot()
    {
        return Quaternion.Euler((RightPitch + LeftPitch) / 2f, RightYaw, 0.0f);
    }

    public float GetCloseAmount()
    {
        return CloseAmount;
    }

    public void SetLeftEyeRot(Quaternion rot)
    {
        LeftPitch = rot.eulerAngles.x;
        LeftYaw = rot.eulerAngles.y;
    }

    public void SetRightEyeRot(Quaternion rot)
    {
        RightPitch = rot.eulerAngles.x;
        RightYaw = rot.eulerAngles.y;
    }

    public void SetCloseAmount(float closeAmount)
    {
        CloseAmount = closeAmount;
    }

    private void UpdateBones()
    {
        if (Owner == null) return;

        var boneDefinitions = Owner.PlayerVisuals.GetComponent<RigDefinition>().boneDefinitions;

        var leftEyeBone = boneDefinitions[32].Transform;
        leftEyeBone.transform.localRotation = GetLeftEyeRot() * Quaternion.Euler(90f, 0f, 0f);

        var rightEyeBone = boneDefinitions[33].Transform;
        rightEyeBone.transform.localRotation = GetRightEyeRot() * Quaternion.Euler(90f, 0f, 0f);


        {
            var leftEyelidBone = boneDefinitions[27].Transform;
            Quaternion closedRot = Quaternion.Euler(-110f, leftEyelidBone.localEulerAngles.y, leftEyelidBone.localEulerAngles.z);
            Quaternion openRot = Quaternion.Euler(-65f, leftEyelidBone.localEulerAngles.y, leftEyelidBone.localEulerAngles.z);
            leftEyelidBone.localRotation = Quaternion.Slerp(openRot, closedRot, CloseAmount);
        }

        {
            var rightEyelidBone = boneDefinitions[28].Transform;
            Quaternion closedRot = Quaternion.Euler(-110f, rightEyelidBone.localEulerAngles.y, rightEyelidBone.localEulerAngles.z);
            Quaternion openRot = Quaternion.Euler(-65f, rightEyelidBone.localEulerAngles.y, rightEyelidBone.localEulerAngles.z);
            rightEyelidBone.localRotation = Quaternion.Slerp(openRot, closedRot, CloseAmount);
        }

        /*
         * Lower L: 21
         * Lower R: 22
         * Upper L: 27
         * Upper R: 28
         */
    }
}
