using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    internal sealed class ChrononPack : ItemComponent, IServerSerializable
    {
        private bool left, right;
        private double leftTap = -10, rightTap = -10, ready;
        private int direction;
        private float time, distance;
        private float dashLimit = 6;
        private Vector2 previous;
        private Character owner;
        private Submarine sub;
        private readonly HashSet<PhysicsBody> bodies = new();
        private byte evasionSequence;
        private double nextEvasionVisual;
        internal bool CanEvadeTorso(Character c)
        {
            if (GameMain.NetworkMember?.IsClient == true || !Valid(c) || Dashing
                || !c.AnimController.OnGround || c.AnimController.Collider.LinearVelocity.LengthSquared() > 0.04f
                || c.IsKeyDown(InputType.Left) || c.IsKeyDown(InputType.Right)
                || c.IsKeyDown(InputType.Up) || c.IsKeyDown(InputType.Down)) return false;
            return true;
        }
        internal void ShowEvasion(Character c)
        {
            // The shared projectile filter owns the roll; only visuals are throttled.
            if (Timing.TotalTime >= nextEvasionVisual)
            {
                nextEvasionVisual = Timing.TotalTime + 0.18;
                unchecked { evasionSequence++; }
#if CLIENT
                EvasionBurst(c);
#endif
                Event();
            }
        }
        internal bool Dashing => time > 0;
        internal static ChrononPack Equipped(Character c) => c?.Inventory?.GetItemInLimbSlot(InvSlotType.Bag)?.GetComponent<ChrononPack>();
        private bool Valid(Character c) => MovementState.CanDash(c) && Equipped(c) == this
            && Item.Condition > 0 && !ActiveCrawl.IsCrawling(c);
        public ChrononPack(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        private void Event()
        {
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        internal bool TryAIDash(Character c, int sign, float metres)
        {
            if (GameMain.NetworkMember?.IsClient == true || !c.IsAIControlled || Dashing
                || Timing.TotalTime < ready || !Valid(c) || metres < 0.3f) return false;
            owner = c; sub = c.Submarine; previous = c.AnimController.Collider.SimPosition;
            direction = sign < 0 ? -1 : 1; time = 0.4f; distance = 0;
            dashLimit = Math.Clamp(metres, 0.3f, 6); ready = Timing.TotalTime + 1.5;
            Event(); return true;
        }
        public override void Update(float deltaTime, Camera cam)
        {
            var c = Item.ParentInventory?.Owner as Character;
#if CLIENT
            Sample(c);
#endif
            if (GameMain.NetworkMember?.IsClient == true) return;
            bool l = c?.IsKeyDown(InputType.Left) == true, r = c?.IsKeyDown(InputType.Right) == true;
            void Tap(int sign, ref double last)
            {
                if (Timing.TotalTime - last <= 0.25 && !Dashing && Timing.TotalTime >= ready && Valid(c))
                {
                    owner = c; sub = c.Submarine; previous = c.AnimController.Collider.SimPosition;
                    direction = sign; time = 0.4f; distance = 0; ready = Timing.TotalTime + 1.5;
                    dashLimit = 6;
                    last = -10; Event();
                }
                else last = Timing.TotalTime;
            }
            if (c != owner && !Dashing) { leftTap = rightTap = -10; owner = c; }
            if (c?.IsAIControlled != true)
            {
                if (l && !left && !r) Tap(-1, ref leftTap);
                if (r && !right && !l) Tap(1, ref rightTap);
            }
            left = l; right = r;
            if (!Dashing) return;
            if (c != owner || !Valid(c) || c.Submarine != sub) { Stop(); return; }
            Vector2 p = c.AnimController.Collider.SimPosition;
            distance += Math.Max(0, (p.X - previous.X) * direction); previous = p;
            time -= deltaTime;
            if (distance >= dashLimit || time <= 0) Stop();
        }
        private void Stop()
        {
            if (owner != null && !owner.Removed && owner.AnimController != null)
                Velocity(owner, Math.Min(owner.AnimController.GetSpeed(AnimationType.Run), 5));
            if (owner?.AnimController != null) owner.AnimController.strongestImpact = 0;
            time = 0; Event();
        }
        private void Velocity(Character c, float speed)
        {
            MovementState.DashVelocity(c, direction, speed, bodies);
        }
        internal void Move(HumanoidAnimController a, float dt)
        {
            if (!Dashing || !Valid(a.Character) || GameMain.NetworkMember?.IsClient == true) return;
            float step = Math.Min(24 * dt, Math.Max(0, dashLimit - distance));
            if (step <= 0) { Stop(); return; }
            float closest = 1;
            for (int i = -1; i <= 1; i++)
            {
                Vector2 p = a.Collider.SimPosition + new Vector2(0, i * a.Collider.Height * 0.5f);
                GameMain.World.RayCast((f, point, normal, fraction) =>
                {
                    if (f.IsSensor || f.Body.UserData is Limb || f.Body.UserData is Character) return -1;
                    if (f.Body.UserData is Structure s && (s.IsPlatform || s.Submarine != a.Character.Submarine)) return -1;
                    // Loose equipment is not terrain. Closed doors remain an obstacle.
                    if (f.Body.UserData is Item item && (item.ParentInventory != null
                        || item.Submarine != a.Character.Submarine || item.GetComponent<Door>() == null)) return -1;
                    closest = Math.Min(closest, fraction); return fraction;
                }, p, p + new Vector2(direction * (step + a.Collider.Radius + 0.05f), 0));
            }
            if (closest < 1) { Stop(); return; }
            a.TargetDir = direction > 0 ? Direction.Right : Direction.Left;
            Velocity(a.Character, step / Math.Max(dt, 0.001f));
        }
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        { msg.WriteBoolean(Dashing); msg.WriteBoolean(direction > 0); msg.WriteByte(evasionSequence); }
#endif
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            time = msg.ReadBoolean() ? 0.4f : 0; direction = msg.ReadBoolean() ? 1 : -1;
            byte sequence = msg.ReadByte();
            if (sequence != evasionSequence) EvasionBurst(Item.ParentInventory?.Owner as Character);
            evasionSequence = sequence;
        }
        private void EvasionBurst(Character c)
        {
            if (c == null || c.Removed) return;
            for (int axis = 0; axis < 4; axis++)
            {
                Vector2 direction = new Vector2((float)Math.Cos(axis * MathHelper.PiOver2), (float)Math.Sin(axis * MathHelper.PiOver2));
                for (int step = 1; step <= 3; step++)
                    FoehnDashAfterimages.Capture(Item, c, direction * (step * 22));
            }
            SonicRefraction.AddChrononBurst(c.WorldPosition);
        }
        private readonly List<(Vector2 P, double T, Submarine Sub)> trail = new();
        private bool visualActive;
        private double nextSample;
        private double flashUntil;
        private void Sample(Character c)
        {
            trail.RemoveAll(p => Timing.TotalTime - p.T > 0.65 || p.Sub?.Removed == true);
            if (c == null || c.Removed) return;
            if (visualActive != Dashing)
            {
                SonicRefraction.AddChrononBurst(c.WorldPosition); visualActive = Dashing;
                flashUntil = Timing.TotalTime + 0.12;
                var endingTorso = c.AnimController.GetLimb(LimbType.Torso);
                trail.Add(((endingTorso?.WorldPosition ?? c.WorldPosition) - (c.Submarine?.Position ?? Vector2.Zero), Timing.TotalTime, c.Submarine));
            }
            if (!Dashing || Timing.TotalTime < nextSample) return;
            nextSample = Timing.TotalTime + 0.02;
            var torso = c.AnimController.GetLimb(LimbType.Torso);
            Vector2 p = torso?.WorldPosition ?? c.WorldPosition;
            if (trail.Count > 0 && trail[trail.Count - 1].Sub == c.Submarine)
                SonicRefraction.Add(trail[trail.Count - 1].P + (c.Submarine?.Position ?? Vector2.Zero), p);
            trail.Add((p - (c.Submarine?.Position ?? Vector2.Zero), Timing.TotalTime, c.Submarine));
            FoehnDashAfterimages.Capture(Item, c);
        }
        internal void Draw(SpriteBatch batch)
        {
            var c = Item.ParentInventory?.Owner as Character;
            float flash = MathHelper.Clamp((float)(flashUntil - Timing.TotalTime) / 0.12f, 0, 1);
            if (flash > 0 && c != null)
            {
                var torso = c.AnimController.GetLimb(LimbType.Torso);
                Vector2 p = (torso?.WorldPosition ?? c.WorldPosition) + new Vector2(-c.AnimController.Dir * 18, 12)
                    + (c.Submarine == null ? Vector2.Zero : c.Submarine.DrawPosition - c.Submarine.Position);
                p.Y = -p.Y;
                GUI.DrawLine(batch, p - Vector2.UnitX * 16, p + Vector2.UnitX * 16, new Color(255, 135, 15) * (flash * 0.35f), width: 24 * flash);
                GUI.DrawLine(batch, p - Vector2.UnitX * 10, p + Vector2.UnitX * 10, new Color(255, 233, 155) * flash, width: 5 * flash);
            }
            for (int i = 1; i < trail.Count; i++)
            {
                var p = trail[i]; var old = trail[i - 1];
                if (p.Sub != old.Sub) continue;
                float life = MathHelper.Clamp(1 - (float)(Timing.TotalTime - p.T) / 0.65f, 0, 1);
                Vector2 a = old.P + (old.Sub?.DrawPosition ?? Vector2.Zero), b = p.P + (p.Sub?.DrawPosition ?? Vector2.Zero);
                a.Y = -a.Y; b.Y = -b.Y;
                for (int j = 0; j < 3; j++)
                {
                    Vector2 offset = new Vector2(0, (j - 1) * 12 + (float)Math.Sin(i * 1.7 + j) * 4);
                    float along = i / (float)Math.Max(1, trail.Count - 1);
                    float heat = along * life;
                    float fade = life * life;
                    float width = MathHelper.Lerp(0.5f, 7f, heat);
                    Color color = Color.Lerp(new Color(225, 55, 8), new Color(255, 249, 210), heat);
                    GUI.DrawLine(batch, a + offset, b + offset, new Color(255, 115, 12) * (fade * 0.18f), width: width * 3);
                    GUI.DrawLine(batch, a + offset, b + offset, color * (fade * 0.85f), width: width);
                }
            }
        }
#endif
    }
    public sealed class ChrononPackPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.chronon.pack");
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Character)))
                if (method.Name == "AddDamage") harmony.Patch(method, prefix: new HarmonyMethod(typeof(ChrononPackPlugin), nameof(CharacterDamage)) { priority = Priority.First });
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Limb)))
                if (method.Name == "AddDamage") harmony.Patch(method, prefix: new HarmonyMethod(typeof(ChrononPackPlugin), nameof(LimbDamage)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(CharacterHealth), "ApplyDamage"), prefix: new HarmonyMethod(typeof(ChrononPackPlugin), nameof(HealthDamage)));
            harmony.Patch(AccessTools.Method(typeof(Character), "SetStun"), prefix: new HarmonyMethod(typeof(ChrononPackPlugin), nameof(Stun)));
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"), prefix: new HarmonyMethod(typeof(ChrononPackPlugin), nameof(BeforeAnimation)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"), postfix: new HarmonyMethod(typeof(ChrononPackPlugin), nameof(Motion)) { priority = Priority.Last });
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(Limb), "Draw", new[] { typeof(SpriteBatch), typeof(Camera), typeof(Color?), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(ChrononPackPlugin), nameof(Fade)), postfix: new HarmonyMethod(typeof(ChrononPackPlugin), nameof(Draw)));
#endif
        }
        private static bool Protected(Character c) => ChrononPack.Equipped(c)?.Dashing == true;
        // The numeric AttackResult constructor leaves Afflictions null. CharacterHealth
        // still enumerates it after an evaded limb hit, so return a valid empty list.
        private static AttackResult EvadedHit(Limb limb)
            => new AttackResult(new List<Affliction>(), limb, new List<DamageModifier>());
        private static bool CharacterDamage(Character __instance, ref AttackResult __result)
        { if (!Protected(__instance)) return true; __result = EvadedHit(null); return false; }
        private static bool LimbDamage(Limb __instance, ref AttackResult __result)
        { if (!Protected(__instance.character)) return true; __result = EvadedHit(__instance); return false; }
        private static bool Stun(Character __instance) => !Protected(__instance);
        private static bool HealthDamage(CharacterHealth __instance) => !Protected(__instance.Character);
        private static void BeforeAnimation(HumanoidAnimController __instance)
        { if (Protected(__instance.Character)) __instance.strongestImpact = 0; }
        private static void Motion(HumanoidAnimController __instance, float __0) => ChrononPack.Equipped(__instance.Character)?.Move(__instance, __0);
#if CLIENT
        private static void Fade(Limb __instance, ref Color? __2)
        {
            if (ChrononPack.Equipped(__instance.character)?.Dashing == true)
                __2 = (__2 ?? Color.White) * 0.4f;
        }
        private static void Draw(Limb __instance, SpriteBatch __0)
        { if (__instance.type == LimbType.Torso) ChrononPack.Equipped(__instance.character)?.Draw(__0); }
#endif
        public void Dispose() => harmony?.UnpatchSelf();
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
