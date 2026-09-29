using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class BackpackAttachments : IAssemblyPlugin
    {
        private sealed class Attachment
        {
            internal Item Backpack;
            internal Character Wearer;
            internal Limb Torso;
            internal float Facing;
        }
        private static readonly Dictionary<Projectile, Attachment> attachments = new Dictionary<Projectile, Attachment>();
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.backpack.attachments");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Projectile), "StickToTarget"),
                    prefix: new HarmonyMethod(typeof(BackpackAttachments), nameof(BeginStick)),
                    postfix: new HarmonyMethod(typeof(BackpackAttachments), nameof(EndStick)));
                harmony.Patch(AccessTools.Method(typeof(Projectile), "Update"),
                    prefix: new HarmonyMethod(typeof(BackpackAttachments), nameof(UpdateAttachment)));
                harmony.Patch(AccessTools.Method(typeof(Projectile), "Unstick"),
                    postfix: new HarmonyMethod(typeof(BackpackAttachments), nameof(Forget)));
#if SERVER
                harmony.Patch(AccessTools.Method(typeof(Projectile), "ServerEventWrite"),
                    postfix: new HarmonyMethod(typeof(BackpackAttachments), nameof(WriteAttachment)));
#endif
#if CLIENT
                harmony.Patch(AccessTools.Method(typeof(Projectile), "ClientEventRead"),
                    postfix: new HarmonyMethod(typeof(BackpackAttachments), nameof(ReadAttachment)));
#endif
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC BackpackAttachments] " + error);
            }
        }

        private static void BeginStick(Projectile __instance, ref Body targetBody, out Attachment __state)
        {
            __state = null;
            if (GameMain.NetworkMember?.IsClient == true || __instance.IsStuckToTarget
                || !(targetBody?.UserData is Item backpack)
                || (backpack.GetComponent<SpaceBackpackDamage>() == null && backpack.GetComponent<RecallBackpack>() == null)) return;
            var user = backpack.ParentInventory?.Owner as Character;
            if (user == null || user.Removed || backpack.Condition <= 0
                || user.GetEquippedItem(backpack.Prefab.Identifier, InvSlotType.Bag) != backpack) return;
            var torso = user.AnimController?.GetLimb(LimbType.Torso);
            if (torso?.body == null || torso.Removed || torso.IsSevered) return;
            __state = new Attachment { Backpack = backpack, Wearer = user, Torso = torso, Facing = user.AnimController.Dir };
            // Native limb IDs replicate reliably; the auxiliary backpack body has no network ID.
            targetBody = torso.body.FarseerBody;
        }

        private static void EndStick(Projectile __instance, Attachment __state)
        {
            if (__state != null && __instance.StickTarget == __state.Torso.body.FarseerBody)
                attachments[__instance] = __state;
        }

        internal static Vector2 Mirror(Vector2 vector, float rotation)
        {
            Vector2 local = Vector2.Transform(vector, Matrix.CreateRotationZ(-rotation));
            local.X = -local.X;
            return Vector2.Transform(local, Matrix.CreateRotationZ(rotation));
        }

        private static void UpdateAttachment(Projectile __instance)
        {
            if (!attachments.TryGetValue(__instance, out var state)) return;
            var item = __instance.Item;
            var user = state.Wearer;
            var torso = state.Torso;
            if (item.Removed || !__instance.IsStuckToTarget)
            { attachments.Remove(__instance); return; }
            if (state.Backpack.Removed || state.Backpack.Condition <= 0 || user.Removed || torso.Removed || torso.IsSevered
                || user.GetEquippedItem(state.Backpack.Prefab.Identifier, InvSlotType.Bag) != state.Backpack
                || __instance.StickTarget != torso.body.FarseerBody)
            {
                __instance.Unstick();
                Sync(__instance);
                return;
            }
            float facing = user.AnimController.Dir;
            if (facing == state.Facing) return;
            // Mirror the current offset, including any sliding of a prismatic projectile.
            Vector2 offset = FarseerPhysics.ConvertUnits.ToSimUnits(item.WorldPosition - torso.WorldPosition);
            Vector2 position = torso.SimPosition + Mirror(offset, torso.Rotation);
            Vector2 direction = Mirror(new Vector2((float)Math.Cos(item.body.Rotation), (float)Math.Sin(item.body.Rotation)), torso.Rotation);
            Vector2 axis = Mirror(__instance.jointAxis, torso.Rotation);
            float remaining = __instance.stickTimer;
            ReleaseJointForTurn(__instance);
            item.SetTransform(position, (float)Math.Atan2(direction.Y, direction.X), forceSubmarine: torso.Submarine);
            __instance.StickToTarget(torso.body.FarseerBody, axis);
            // Turning must not extend StickDuration or restore an expired attachment.
            __instance.stickTimer = remaining;
            state.Facing = facing;
            attachments[__instance] = state;
            Sync(__instance);
        }

        private static void Forget(Projectile __instance) => attachments.Remove(__instance);
        private static void ReleaseJointForTurn(Projectile projectile)
        {
            // Unstick also snaps Rope components. Turning is a joint replacement, not a
            // detachment: retain harpoon ropes and the projectile's native collision state.
            projectile.StickTarget = null;
            if (projectile.stickJoint != null)
            {
                projectile.stickJoint.Enabled = false;
                if (GameMain.World.JointList.Contains(projectile.stickJoint)) GameMain.World.Remove(projectile.stickJoint);
                projectile.stickJoint = null;
            }
            if (projectile.stickTargetCharacter != null)
            {
                projectile.stickTargetCharacter.AttachedProjectiles.Remove(projectile);
                projectile.stickTargetCharacter = null;
            }
        }
#if SERVER
        private static void WriteAttachment(Projectile __instance, IWriteMessage msg)
        {
            bool tracked = __instance.IsStuckToTarget && attachments.ContainsKey(__instance);
            msg.WriteBoolean(tracked);
            if (!tracked) return;
            msg.WriteSingle(__instance.Item.body.Rotation);
            msg.WriteSingle(__instance.jointAxis.X);
            msg.WriteSingle(__instance.jointAxis.Y);
            msg.WriteSingle(__instance.stickTimer);
        }
#endif
#if CLIENT
        private static void ReadAttachment(Projectile __instance, IReadMessage msg)
        {
            if (!msg.ReadBoolean()) return;
            float rotation = msg.ReadSingle();
            Vector2 axis = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            float remaining = msg.ReadSingle();
            var item = __instance.Item;
            var target = __instance.StickTarget;
            if (item.Removed || item.body == null || target == null) return;
            // Native repeated stick events move the body but leave an existing joint intact.
            // Rebuild after reading the native destination, using the server's mirrored rotation.
            ReleaseJointForTurn(__instance);
            item.SetTransform(item.SimPosition, rotation);
            __instance.StickToTarget(target, axis);
            __instance.stickTimer = remaining;
        }
#endif
        private static void Sync(Projectile projectile)
        {
#if SERVER
            if (!projectile.Item.Removed)
                projectile.Item.CreateServerEvent(projectile, new Projectile.EventData(launch: false));
#endif
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            foreach (var projectile in attachments.Keys.ToArray())
                if (!projectile.Item.Removed) { projectile.Unstick(); Sync(projectile); }
            attachments.Clear();
            harmony?.UnpatchSelf();
        }
    }
}
