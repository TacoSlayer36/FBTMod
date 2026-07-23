using Il2CppRUMBLE.Players;
using MelonLoader;
using ReplayMod.Replay;
using ReplayMod.Replay.Serialization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using static FBTMod.Main;

namespace FBTMod;

public class ETReplayExtension
{

    public static ETReplayExtension instance;
    public ETReplayExtension() => instance = this;

    internal static ETState lastState;

    internal static MelonPreferences_Entry<bool> RecordET;

    private enum ETField : byte
    {
        LeftRotation,
        RightRotation,
        CloseAmount
    }

    internal class ETExtension : ReplayExtension
    {
        public override string Id => "ETSupport";

        public override void OnRecordFrame(Frame frame, bool isBuffer)
        {
            if (!RecordET.Value)
                return;

            if (ReplayMod.Core.Main.Recording.RecordedPlayers.Count == 0) return;
            if (!ReplayAPI.IsRecording) return;

            Dictionary<byte, PlayerETData> newDatas = new();
            for (byte i = 0; i < ReplayMod.Core.Main.Recording.RecordedPlayers.Count; i++)
            {
                Player player = ReplayMod.Core.Main.Recording.RecordedPlayers[i];
                if (player == null) continue;

                EyeTracking ET = player.Controller.GetComponent<EyeTracking>();
                if (ET.Disabled || ET.Type is FullBodyTracking.FBTType.None) continue;

                newDatas[i] = new PlayerETData
                {
                    LeftRot = ET.GetLeftEyeRot(),
                    RightRot = ET.GetRightEyeRot(),
                    CloseAmount = ET.GetCloseAmount()
                };
            }

            if (newDatas.Values.Count == 0) return;

            frame.SetExtensionData(this, new ETState
            {
                ETDatas = newDatas
            }.Clone());
        }

        public override void OnWriteFrame(ReplayAPI.FrameExtensionWriter writer, Frame frame)
        {
            // If this frame has no recorded data, write nothing.
            if (!frame.TryGetExtensionData(this, out ETState state))
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

            foreach (var (ownerId, etData) in state.ETDatas)
            {
                var subIndex = ownerId;
                writer.WriteChunk(subIndex, w =>
                {
                    w.Write(ETField.LeftRotation, etData.LeftRot);
                    w.Write(ETField.RightRotation, etData.RightRot);
                    w.Write(ETField.CloseAmount, etData.CloseAmount);
                });
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

            var state = ReplaySerializer.ReadChunk<ETState, ETField>(
                br,
                () => lastState?.Clone() ?? new ETState(),
                (s, field, size, reader) =>
                {
                    var ownerId = subIndex;

                    if (!s.ETDatas.ContainsKey((byte)ownerId))
                    {
                        s.ETDatas[(byte)ownerId] = new PlayerETData();
                    }

                    var etData = s.ETDatas[(byte)ownerId];

                    switch (field)
                    {
                        case ETField.LeftRotation:
                            etData.LeftRot = reader.ReadQuaternion();
                            break;

                        case ETField.RightRotation:
                            etData.RightRot = reader.ReadQuaternion();
                            break;

                        case ETField.CloseAmount:
                            etData.CloseAmount = reader.ReadDouble();
                            break;
                    }
                });

            frame.SetExtensionData(this, state);
            lastState = state;
        }

        public override void OnPlaybackFrame(Frame frame, Frame nextFrame)
        {
            foreach (EyeTracking et in Main.AllETs)
            {
                if (et.Type is FullBodyTracking.FBTType.Animated)
                    et.Disabled = true;
            }

            if (!frame.TryGetExtensionData(this, out ETState state))
                return;

            if (state == null) return;

            foreach (var (ownerId, etData) in state.ETDatas)
            {
                PlayerController player = ReplayMod.Core.Main.Playback.PlaybackPlayers[ownerId].Controller;
                EyeTracking ET = player.GetComponent<EyeTracking>();
                if (ET == null) return;

                ET.Disabled = false;
                ET.SetLeftEyeRot(etData.LeftRot);
                ET.SetRightEyeRot(etData.RightRot);
                ET.SetCloseAmount((float)etData.CloseAmount);
            }
        }
    }

    internal class ETState
    {
        public Dictionary<byte, PlayerETData> ETDatas = new();

        public ETState Clone()
        {
            Dictionary<byte, PlayerETData> newDatas = new();
            foreach (var (ownerId, playerETData) in ETDatas)
            {
                newDatas[ownerId] = new PlayerETData()
                {
                    LeftRot = playerETData.LeftRot,
                    RightRot = playerETData.RightRot,
                    CloseAmount = playerETData.CloseAmount
                };
            }

            return new ETState
            {
                ETDatas = newDatas
            };
        }
    }
    internal class PlayerETData
    {
        public Quaternion LeftRot;
        public Quaternion RightRot;
        public double CloseAmount;
    }
}