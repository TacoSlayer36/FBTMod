using Il2CppPlayFab.ClientModels;
using Il2CppRUMBLE.Managers;
using Il2CppRUMBLE.Players;
using MelonLoader;
using ReplayMod;
using ReplayMod.Replay;
using ReplayMod.Replay.Serialization;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using static FBTMod.Main;
using PoseDict = System.Collections.Generic.Dictionary<FBTMod.Main.TrackerRole, FBTMod.Main.Pose>;

namespace FBTMod;

public class ReplayModExtension
{
    // This example demonstrates how to extend replays by recording
    // and replaying a scene object

    public static ReplayModExtension instance;
    public ReplayModExtension() => instance = this;

    // Used when reading frames to allow state to carry forward from delta-compression
    // Delta-compression is not used in this example, but is highly recommended.
    internal static FBTState lastState;

    internal static MelonPreferences_Entry<bool> RecordFBT;

    // Field identifiers used when writing frame data.
    // These values are serialized as byte tags and must remain in a stable order.
    private enum FBTField : byte
    {
        Position,
        Rotation,
        Id
    }

    internal class FBTExtension : ReplayExtension
    {
        public override string Id => "FBTSupport";

        public override void OnRecordFrame(Frame frame, bool isBuffer)
        {
            if (!RecordFBT.Value)
                return;

            Dictionary<byte, PoseDict> transforms = new();
            for (byte i = 0; i < ReplayMod.Core.Main.Recording.RecordedPlayers.Count; i++)
            {
                Player player = ReplayMod.Core.Main.Recording.RecordedPlayers[i];
                if (player == null) return;
                FullBodyTracking fbt = player.Controller.GetComponent<FullBodyTracking>();
                transforms[i] = fbt.RuntimeTrackerTransforms;
            }

            frame.SetExtensionData(this, new FBTState
            {
                TrackerTransforms = transforms
            }.Clone());
        }

        public override void OnWriteFrame(ReplayAPI.FrameExtensionWriter writer, Frame frame)
        {
            // If this frame has no recorded data, write nothing.
            if (!frame.TryGetExtensionData(this, out FBTState state))
                return;

            /*
             * Each Write(field, value) call writes:
             *   - Field ID (1 byte)
             *   - Field payload length (1 byte)
             *   - The actual data (N bytes)
             *
             * The mod groups these field entries into a single chunk
             * for this extension automatically.
             *
             * IMPORTANT:
             *   - Only write fields that changed between frames (delta encoding recommended).
             *   - Do NOT manually write field IDs or lengths using raw bw.Write.
             *     Always use the provided BinaryWriter.Write(field, value) overloads.
            */

            foreach (var (ownerId, poseDict) in state.TrackerTransforms)
            {
                foreach (var transform in poseDict)
                {
                    var subIndex = (ownerId << 8) | (int)transform.Key;
                    writer.WriteChunk(subIndex, w =>
                    {
                        w.Write(FBTField.Position, transform.Value.position);
                        w.Write(FBTField.Rotation, transform.Value.rotation);
                    });
                }
            }
        }

        public override void OnReadFrame(BinaryReader br, Frame frame, int subIndex)
        {
            /*
             * ReadChunk builds a state object for this frame.
             *
             * The ctor function used to create the initial state for this frame.
             * Each field encountered in the chunk mutates the state via the callback.
             *
             * When finished, ReadChunk returns the fully reconstructed  state.
             *
             * Unknown fields are automatically skipped.
             *
             * Technically, the ctor function here is unnecessary due to our lack of delta-compression,
             * but it is highly recommended to do so.
             */
            
            var state = ReplaySerializer.ReadChunk<FBTState, FBTField>(
                br,
                () => lastState?.Clone() ?? new FBTState(),
                (s, field, size, reader) =>
                {
                    var ownerId = (subIndex >> 8) & 0xff;
                    var trackerRole = (subIndex) & 0xff;

                    if (!s.TrackerTransforms.ContainsKey((byte)ownerId))
                    {
                        s.TrackerTransforms[(byte)ownerId] = new PoseDict();
                    }

                    var poseDict = s.TrackerTransforms[(byte)ownerId];

                    if (!poseDict.ContainsKey((TrackerRole)trackerRole))
                    {
                        poseDict[(TrackerRole)trackerRole] = new Main.Pose
                        {
                            position = Vector3.zero,
                            rotation = Quaternion.identity
                        };
                    }

                    switch (field)
                    {
                        case FBTField.Position:
                            poseDict[(TrackerRole)trackerRole].position = reader.ReadVector3();
                            break;

                        case FBTField.Rotation:
                            poseDict[(TrackerRole)trackerRole].rotation = reader.ReadQuaternion();
                            break;
                    }
                });

            frame.SetExtensionData(this, state);
            lastState = state;
        }

        // NextFrame should be used for interpolation, though that isn't implemented here.
        public override void OnPlaybackFrame(Frame frame, Frame nextFrame)
        {
            if (!frame.TryGetExtensionData(this, out FBTState state))
                return;

            if (state == null) return;

            foreach (var (ownerId, poseDict) in state.TrackerTransforms)
            {
                PlayerController player = ReplayMod.Core.Main.Playback.PlaybackPlayers[ownerId].Controller;
                FullBodyTracking fbt = player.GetComponent<FullBodyTracking>();
                if (fbt == null || fbt.Type is not FullBodyTracking.FBTType.Animated) return;
                if (fbt.LeftLegSolver == null || fbt.RightLegSolver == null) return;
                fbt.RuntimeTrackerTransforms = poseDict;
                fbt.LeftLegSolver.FootTarget = poseDict[TrackerRole.LeftFoot];
                fbt.RightLegSolver.FootTarget = poseDict[TrackerRole.RightFoot];
            }
        }
    }

    internal class FBTState
    {
        public Dictionary<byte, PoseDict> TrackerTransforms = new();

        // Used to preserve previous state during reconstruction.
        public FBTState Clone()
        {
            Dictionary<byte, PoseDict> newTransforms = new();
            foreach (var (ownerId, poseDict) in TrackerTransforms)
            {
                Dictionary<TrackerRole, Main.Pose> newPoseDict = new();
                foreach (var transform in poseDict)
                {
                    newPoseDict[transform.Key] = new Main.Pose()
                    {
                        position = transform.Value.position,
                        rotation = transform.Value.rotation
                    };
                }
                newTransforms[ownerId] = newPoseDict;
            }

            return new FBTState
            {
                TrackerTransforms = newTransforms
            };
        }
    }
}