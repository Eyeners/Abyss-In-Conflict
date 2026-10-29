using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using HarmonyLib;
using Barotrauma.LuaCs;
#if CLIENT
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    partial class FoehnCharge
    {
        private int dashDirection;
        private float dashTime, dashDistance;
        private double dashReady;
        private Vector2 dashPrevious;
        private Character dashOwner;
        private Submarine dashSub;
        internal bool Dashing => dashTime > 0;
        private bool DashValid(Character c) => MovementState.CanDash(c)
            && Equipped(c) == this && Item.Condition > 0
            && Item.GetComponent<FoehnSlide>()?.Sliding != true;
        private void StartDash(Character c, int sign)
        {
            // The backpack owns double-tap movement when both are equipped.
            if (ChrononPack.Equipped(c)?.Item.Condition > 0) return;
            if (!Authority || !DashValid(c) || Dashing || Timing.TotalTime < dashReady || (sign != -1 && sign != 1)) return;
            dashOwner = c; dashSub = c.Submarine; dashPrevious = c.AnimController.Collider.SimPosition;
            dashDirection = sign; dashDistance = 0; dashTime = 0.35f;
            dashReady = Timing.TotalTime + 1.5; SpeedPenalty = 0; ramp = 1; direction = sign;
            ApplyDashVelocity(c, 15);
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        private void ApplyDashVelocity(Character c, float speed)
        {
            MovementState.DashVelocity(c, dashDirection, speed, bodies);
        }
        private void StopDash(Character c, bool finish)
        {
            ReleaseDashPose();
            dashTime = 0;
            if (finish && c?.AnimController != null)
            {
                float run = c.AnimController.GetSpeed(AnimationType.Run) * Math.Max(1, RunMultiplier) * c.SpeedMultiplier;
                ApplyDashVelocity(c, Math.Clamp(run, 0, 15));
                ramp = 1;
            }
            dashOwner = null;
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        private void UpdateDash(Character c, float dt)
        {
            if (!Dashing || !DashValid(c)) ReleaseDashPose();
#if CLIENT
            ReadDashInput(c);
#endif
            if (!Authority || !Dashing) return;
            if (c != dashOwner || !DashValid(c) || c.Submarine != dashSub) { StopDash(c, false); return; }
            var position = c.AnimController.Collider.SimPosition;
            dashDistance += Math.Max(0, (position.X - dashPrevious.X) * dashDirection);
            dashPrevious = position; dashTime -= dt;
            // Actual travelled distance, with a short timeout so a wall cannot pin the dash active.
            if (dashDistance >= 3 || dashTime <= 0) StopDash(c, true);
        }
        internal void DashMotion(Character c, float dt)
        {
            if (!Dashing || !DashValid(c)) return;
            PoseDash(c);
#if CLIENT
            dashSteam -= dt;
            if (dashSteam <= 0)
            {
                dashSteam = 0.035f;
                Item.GetComponent<FoehnJetJump>()?.DrawDashExhaust(c, dashDirection);
            }
#endif
            if (!Authority) return;
            float speed = Math.Min(15, Math.Max(0, 3 - dashDistance) / Math.Max(0.001f, dt));
            ApplyDashVelocity(c, speed);
        }
#if CLIENT
        private float dashSteam;
        private bool leftHeld, rightHeld;
        private double leftTap = -10, rightTap = -10;
        private readonly record struct DashRequest(int Direction) : IEventData;
        private void ReadDashInput(Character c)
        {
            bool left = PlayerInput.KeyDown(Keys.A), right = PlayerInput.KeyDown(Keys.D);
            bool allowed = c == Character.Controlled && DashValid(c)
                && GUI.KeyboardDispatcher.Subscriber == null && !GUI.InputBlockingMenuOpen;
            void Tap(int sign, ref double last)
            {
                double now = Timing.TotalTime;
                if (now - last <= 0.25)
                {
                    last = -10;
                    if (GameMain.NetworkMember?.IsClient == true) Item.CreateClientEvent(this, new DashRequest(sign));
                    else StartDash(c, sign);
                }
                else last = now;
            }
            if (allowed)
            {
                if (left && !leftHeld && !right) Tap(-1, ref leftTap);
                if (right && !rightHeld && !left) Tap(1, ref rightTap);
            }
            else { leftTap = rightTap = -10; }
            leftHeld = left; rightHeld = right;
        }
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
            => msg.WriteInt32(extraData is Item.ComponentStateEventData data && data.ComponentData is DashRequest request ? request.Direction : 0);
        private void ReadDash(IReadMessage msg) { dashTime = msg.ReadSingle(); dashDirection = msg.ReadInt32(); }
#endif
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client client) => StartDash(client?.Character, msg.ReadInt32());
        private void WriteDash(IWriteMessage msg) { msg.WriteSingle(dashTime); msg.WriteInt32(dashDirection); }
#endif
    }
    public sealed class FoehnDashMotion : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.foehn.dash");
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                prefix: new HarmonyMethod(typeof(FoehnDashMotion), nameof(Before)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(FoehnDashMotion), nameof(After)) { priority = Priority.Last });
        }
        private static void Before(HumanoidAnimController __instance)
            => FoehnCharge.Equipped(__instance.Character)?.PrepareDashPose(__instance);
        private static void After(HumanoidAnimController __instance, float __0)
            => FoehnCharge.Equipped(__instance.Character)?.DashMotion(__instance.Character, __0);
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }
    }
}
