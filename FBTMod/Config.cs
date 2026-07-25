using UIFramework;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Playables;

namespace FBTMod;

internal static class Config
{
    internal const string CONFIG_FILE = "config.cfg";

    internal static MelonPreferences_Category BodySettings;
    internal static MelonPreferences_Category EyeSettings;
    internal static MelonPreferences_Entry<bool> EnableEyeTracking;
    internal static MelonPreferences_Entry<bool> ShowTrackingMarkers;
    internal static MelonPreferences_Entry<bool> ShowGazeVisualizer;
    internal static MelonPreferences_Entry<float> EyeSeparationOffset;

    public static void SetUp()
    {
        // Create UI
        BodySettings = MelonPreferences.CreateCategory("FBTMod_FullBodySettings", "Full-Body Tracking");
        BodySettings.SetFilePath(Path.Combine(Main.USER_DATA, CONFIG_FILE));

        UI.CreateButtonEntry(BodySettings, "Calibrate", "Calibrate",
            "Starts FBT Calibration. Line yourself up with the shown pose, then press both triggers to confirm.",
            () =>
            {
                if (!Main.IsCalibrating)
                    MelonCoroutines.Start(Main.instance.Calibration());
            }
        );

        ShowTrackingMarkers = BodySettings.CreateEntry("FBT_ShowTrackingMarkers", true, "Show Tracking Markers", "Show spheres at the location of each body tracker.");

        FBTReplayExtension.RecordFBT = BodySettings.CreateEntry("FBT_RecordFBT", true, "Record FBT", "Record full body tracking in ReplayMod");

        EyeSettings = MelonPreferences.CreateCategory("FBTMod_EyeSettings", "Eye Tracking");
        EyeSettings.SetFilePath(Path.Combine(Main.USER_DATA, CONFIG_FILE));

        EnableEyeTracking = EyeSettings.CreateEntry("FBT_EnableEyeTracking", true, "Enable Eye Tracking", "Use eye tracking if your hardware supports it.");
        ShowGazeVisualizer = EyeSettings.CreateEntry("FBT_ShowGazeVisualizer", true, "Show Gaze Visualizer", "Show an indicator of where you are looking on the Legacy Camera.");

        ETReplayExtension.RecordET = EyeSettings.CreateEntry("FBT_RecordET", true, "Record ET", "Record eye tracking in ReplayMod");
        EyeSeparationOffset = EyeSettings.CreateEntry("FBT_EyeSeparationOffset", -15f, "Eye Separation", "Offset how far apart your eyes are looking");

        UI.RegisterMelon(Main.instance, BodySettings, EyeSettings);

        ShowTrackingMarkers.OnEntryValueChanged.Subscribe(Main.OnShowTrackersToggled);
        EnableEyeTracking.OnEntryValueChanged.Subscribe(Main.OnEnableEyeTrackingToggled);
        ShowGazeVisualizer.OnEntryValueChanged.Subscribe(Main.OnShowGazeVisualizerToggled);
    }
}
