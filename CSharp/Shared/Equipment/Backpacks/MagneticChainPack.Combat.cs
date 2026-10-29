using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    internal sealed partial class MagneticChainPack
    {
        private const float AttackReach = 800, TipSpeed = 2400, ReturnTime = 0.22f;
        private static readonly Identifier GrabReleaseId = "aic_ground_adhesion_release".ToIdentifier();
        private sealed class CombatArm
        {
            internal byte Phase; // 0 rest, 1 outgoing, 2 returning, 3 holding
            internal Vector2 Start, End;
            internal Submarine Sub;
            internal double Born;
            internal float Duration, Travel;
            internal bool Powered;
            internal Vector2 Offset => Sub?.Position ?? Vector2.Zero;
            internal Vector2 Position(float age) => Vector2.Lerp(Start, End, MathHelper.Clamp(age / Math.Max(0.01f, Duration), 0, 1)) + Offset;
        }
        private readonly CombatArm[] arms = { new(), new(), new() };
        private readonly HashSet<PhysicsBody> grabbedBodies = new();
        private Attack normalAttack, poweredAttack;
        private Limb grabbed;
        private Character grabOwner;
#if SERVER
        private Client grabClient;
#endif
        private double nextStab, nextGrab;
        private int nextBlade = 1;
#if CLIENT
        private Sprite blade, blueBlade;
        private bool previousQ;
        private double nextAttackRequest;
        private sealed record CombatRequest(byte Kind, Vector2 Cursor) : ItemComponent.IEventData;
#endif
        private void InitCombat(ContentXElement element)
        {
            normalAttack = new Attack(element.GetChildElement("Attack"), Item.Prefab.Identifier.ToString(), Item);
            poweredAttack = new Attack(element.GetChildElement("PoweredAttack"), Item.Prefab.Identifier.ToString(), Item);
#if CLIENT
            blade = new Sprite(element.GetChildElement("bladesprite"));
            blueBlade = new Sprite(element.GetChildElement("bluebladesprite"));
#endif
        }
        private bool CombatValid(Character c) => Timing.TotalTime >= allDisabledUntil && AttackMode && Item.Condition > 0 && MovementState.Conscious(c)
            && Equipped(c) == this && c.AllowInput;
        private Item CombatBattery()
        {
            if (Item.OwnInventory == null) return null;
            foreach (var cell in Item.OwnInventory.AllItems)
                if (!cell.Removed && cell.HasTag("mobilebattery".ToIdentifier()) && cell.Condition >= 5) return cell;
            return null;
        }
        private void StartArm(int index, Character c, Vector2 cursor)
        {
            if (index == 0) { grabOwner = c; aiGrab = c.IsAIControlled; }
            var arm = arms[index];
            Vector2 direction = cursor - c.WorldPosition;
            if (direction.LengthSquared() < 1) direction = Vector2.UnitX * c.AnimController.Dir;
            direction.Normalize();
            arm.Sub = c.Submarine;
            arm.Start = c.WorldPosition - arm.Offset;
            arm.End = arm.Start + direction * AttackReach;
            arm.Born = Timing.TotalTime; arm.Duration = AttackReach / TipSpeed; arm.Travel = 0;
            arm.Phase = 1; arm.Powered = false;
            if (index > 0 && CombatBattery() is Item battery)
            { battery.Condition -= 5; arm.Powered = true; }
            Changed();
        }
        private void ReturnArm(int index, Character c, Vector2 from)
        {
            var arm = arms[index];
            if (index == 0)
            {
                if (aiGrab || c?.IsAIControlled == true) nextAIGrab = Math.Max(nextAIGrab, Timing.TotalTime + 2.5);
                aiGrab = false;
                grabbed = null;
                grabOwner = null;
#if SERVER
                grabClient = null;
#endif
            }
            arm.Start = from - arm.Offset;
            arm.End = (c?.WorldPosition ?? from) - arm.Offset;
            arm.Born = Timing.TotalTime; arm.Duration = ReturnTime; arm.Phase = 2;
            Changed();
        }
        private void RequestCombat(Character c, byte kind, Vector2 cursor)
        {
            if (!CombatValid(c) || !float.IsFinite(cursor.X) || !float.IsFinite(cursor.Y)) return;
            if (kind == 0)
            {
                if (Timing.TotalTime < nextGrab || Timing.TotalTime < armDisabledUntil[0]) return;
                nextGrab = Timing.TotalTime + 0.3;
                if (arms[0].Phase != 0)
                {
                    Vector2 p = grabbed?.WorldPosition ?? arms[0].Position((float)(Timing.TotalTime - arms[0].Born));
                    ReturnArm(0, c, p);
                }
                else StartArm(0, c, cursor);
            }
            else if (kind == 1 && c.IsKeyDown(InputType.Aim) && Timing.TotalTime >= nextStab)
            {
                if (Timing.TotalTime < armDisabledUntil[nextBlade]) nextBlade = nextBlade == 1 ? 2 : 1;
                if (Timing.TotalTime < armDisabledUntil[nextBlade] || arms[nextBlade].Phase != 0) return;
                nextStab = Timing.TotalTime + 0.45;
                StartArm(nextBlade, c, cursor);
                nextBlade = nextBlade == 1 ? 2 : 1;
            }
        }
        private void UpdateCombat(Character c, float dt)
        {
#if CLIENT
            bool q = PlayerInput.KeyDown(Keys.Q);
            if (c == Character.Controlled && CombatValid(c) && GUI.KeyboardDispatcher.Subscriber == null && !GUI.InputBlockingMenuOpen)
            {
                void Send(byte kind)
                {
                    if (GameMain.NetworkMember?.IsClient == true)
                        Item.CreateClientEvent(this, new CombatRequest(kind, c.CursorWorldPosition));
                    else RequestCombat(c, kind, c.CursorWorldPosition);
                }
                if (q && !previousQ) Send(0);
                if (c.IsKeyDown(InputType.Aim) && c.IsKeyDown(InputType.Shoot) && Timing.TotalTime >= nextAttackRequest)
                { Send(1); nextAttackRequest = Timing.TotalTime + 0.46; }
            }
            previousQ = q;
#endif
            if (GameMain.NetworkMember?.IsClient == true) return;
            // Manual grabs belong to the player controlling the original wearer, not its AI replacement.
            if (arms[0].Phase == 1 || arms[0].Phase == 3)
            {
                bool controlling = c != null && c == grabOwner;
                if (aiGrab) controlling &= c?.IsAIControlled == true;
                else
                {
#if CLIENT
                controlling &= Character.Controlled == grabOwner;
#endif
#if SERVER
                controlling &= grabClient != null && grabClient.Character == grabOwner
                    && GameMain.Server.ConnectedClients.Contains(grabClient);
#endif
                }
                if (!controlling)
                    ReturnArm(0, c, grabbed?.WorldPosition ?? arms[0].Position((float)(Timing.TotalTime - arms[0].Born)));
            }
            if (!CombatValid(c))
            {
                bool active = grabbed != null;
                grabbed = null;
                grabOwner = null;
#if SERVER
                grabClient = null;
#endif
                foreach (var arm in arms) { active |= arm.Phase != 0; arm.Phase = 0; }
                if (active) Changed();
                return;
            }
            UpdateAICombat(c);
            for (int i = 0; i < arms.Length; i++)
            {
                var arm = arms[i];
                if (arm.Phase == 0) continue;
                float age = (float)(Timing.TotalTime - arm.Born);
                if (arm.Phase == 3) { DragTarget(c, dt); continue; }
                if (arm.Phase == 2)
                {
                    if (age >= arm.Duration) { arm.Phase = 0; Changed(); }
                    continue;
                }
                Vector2 from = arm.Position(arm.Travel), to = arm.Position(age);
                arm.Travel = age;
                if (Vector2.DistanceSquared(c.WorldPosition, to) > AttackReach * AttackReach * 1.01f)
                { ReturnArm(i, c, from); continue; }
                Fixture hit = Sweep(c, from, to, out Vector2 point, passPlatforms: true);
                Limb hitLimb = hit?.Body.UserData as Limb;
                // Dead ragdolls can leave the active physics broadphase. Fall back to limb-sized pick regions.
                if (i == 0)
                {
                    float nearest = Vector2.DistanceSquared(from, point);
                    foreach (var corpse in Character.CharacterList)
                    {
                        if (corpse == c || corpse.Removed || !corpse.IsDead) continue;
                        foreach (var limb in corpse.AnimController.Limbs)
                        {
                            if (limb.Removed || limb.IsSevered || limb.body == null) continue;
                            Vector2 step = to - from;
                            float length = step.LengthSquared();
                            if (length < 0.001f) continue;
                            float t = MathHelper.Clamp(Vector2.Dot(limb.WorldPosition - from, step) / length, 0, 1);
                            Vector2 closest = from + step * t;
                            float radius = Math.Max(6, ConvertUnits.ToDisplayUnits(limb.body.GetMaxExtent()) * 0.5f);
                            float distance = Vector2.DistanceSquared(from, closest);
                            if (distance > nearest || Vector2.DistanceSquared(closest, limb.WorldPosition) > radius * radius) continue;
                            if (Sweep(c, from, closest, out _, wallsOnly: true, passPlatforms: true) != null) continue;
                            nearest = distance; point = closest; hitLimb = limb;
                        }
                    }
                }
                if (hit != null || hitLimb != null)
                {
                    if (hitLimb is Limb limb && !limb.IsSevered)
                    {
                        if (i == 0)
                        {
                            if (aiGrab && (limb.character != aiCombatTarget || limb.character.IsDead || c.IsOnFriendlyTeam(limb.character)))
                            { ReturnArm(i, c, point); continue; }
                            HoverBike.DismountForGrab(limb.character);
                            if (!limb.character.IsDead && !c.IsOnFriendlyTeam(limb.character))
                                limb.character.SetStun(1.5f);
                            grabbed = limb; heldTime = damageClock = 0; stunClock = 2; arm.Phase = 3; arm.End = point - arm.Offset;
                            Changed(); continue;
                        }
                        if (i > 0 && !limb.character.IsDead && (!c.IsAIControlled || !c.IsOnFriendlyTeam(limb.character)))
                            (arm.Powered ? poweredAttack : normalAttack).DoDamageToLimb(c, limb, point, 1, playSound: true);
                    }
                    ReturnArm(i, c, point);
                }
                else if (age >= arm.Duration) ReturnArm(i, c, to);
            }
        }
        // Sweep each travelled section: damage occurs when the visible tip arrives.
        // Different submarines use separate physics coordinate offsets.
        private Fixture Sweep(Character source, Vector2 from, Vector2 to, out Vector2 hitPoint, bool wallsOnly = false, bool passPlatforms = false)
        {
            Fixture best = null; Vector2 found = to; float nearest = 1;
            if (Vector2.DistanceSquared(from, to) < 0.001f) { hitPoint = to; return null; }
            void Scan(Submarine sub)
            {
                Vector2 offset = sub?.Position ?? Vector2.Zero;
                GameMain.World.RayCast((fixture, p, n, fraction) =>
                {
                    if (fixture.IsSensor || !fixture.Body.Enabled || fraction > nearest) return -1;
                    object obj = fixture.Body.UserData;
                    bool accept = obj is Structure s && !s.Removed && (!passPlatforms || !s.IsPlatform && s.StairDirection == Direction.None) && s.Submarine == sub
                        || obj is Item item && !item.Removed && item.Submarine == sub && item.GetComponent<Door>() != null
                        || !wallsOnly && obj is Limb l && l.character != source && !l.Removed && !l.IsSevered
                            && !l.character.Removed && l.character.Submarine == sub
                        || sub == null && (fixture.CollisionCategories & Physics.CollisionLevel) != 0;
                    if (!accept) return -1;
                    best = fixture; nearest = fraction; found = ConvertUnits.ToDisplayUnits(p) + offset;
                    return fraction;
                }, ConvertUnits.ToSimUnits(from - offset), ConvertUnits.ToSimUnits(to - offset));
            }
            Scan(null); foreach (var sub in Submarine.Loaded) Scan(sub);
            hitPoint = found; return best;
        }
        private void DragTarget(Character c, float dt)
        {
            if (grabbed == null || grabbed.Removed || grabbed.IsSevered || grabbed.character.Removed
                || grabbed.body == null
                || Vector2.DistanceSquared(c.WorldPosition, grabbed.WorldPosition) > AttackReach * AttackReach)
            { ReturnArm(0, c, grabbed?.WorldPosition ?? c.WorldPosition); return; }
            if (Sweep(c, c.WorldPosition, grabbed.WorldPosition, out _, wallsOnly: true, passPlatforms: true) != null)
            { ReturnArm(0, c, grabbed.WorldPosition); return; }
            Vector2 cursor = aiGrab ? AIHoldPosition(c) : c.CursorWorldPosition, delta = cursor - c.WorldPosition;
            if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return;
            if (delta.LengthSquared() > AttackReach * AttackReach) cursor = c.WorldPosition + Vector2.Normalize(delta) * (AttackReach - 15);
            // Move by force, preserving collision. The target cannot teleport through walls.
            Vector2 error = ConvertUnits.ToSimUnits(cursor - grabbed.WorldPosition);
            Vector2 velocity = error * 5;
            if (velocity.LengthSquared() > 100) velocity = Vector2.Normalize(velocity) * 10;
            var target = grabbed.character;
            // Also reject remounting during a hold, before collecting bodies to move.
            HoverBike.DismountForGrab(target);
            if (target.IsDead)
            {
                // Native G-drag wakes resting corpses through SelectedBy. Magnetic
                // grabs do not select the inventory, so reproduce only that wake-up.
                var ragdoll = target.AnimController;
                ragdoll.bodyInRestTimer = 0;
                foreach (var limb in ragdoll.Limbs)
                {
                    if (limb.Removed || limb.IsSevered || limb.body == null) continue;
                    limb.body.PhysEnabled = true;
                    if (limb.body.Enabled) limb.body.FarseerBody.Awake = true;
                }
                if (target.SelectedBy == null) ragdoll.ResetPullJoints();
            }
            if (!target.IsDead)
            {
                var health = target.CharacterHealth;
                var existing = health.GetAffliction(GrabReleaseId);
                if (existing != null) existing.Strength = Math.Max(existing.Strength, 0.3f);
                else if (AfflictionPrefab.Prefabs.TryGet(GrabReleaseId, out var prefab))
                    health.ApplyAffliction(null, prefab.Instantiate(0.3f));
                heldTime += dt;
                if (!c.IsOnFriendlyTeam(target))
                {
                    stunClock -= dt;
                    if (stunClock <= 0) { target.SetStun(0.1f); stunClock = 2; }
                    if (heldTime > 2)
                    {
                        damageClock += dt;
                        if (damageClock >= 1)
                        {
                            float elapsed = Math.Min(damageClock, 1.5f); damageClock = 0;
                            float progress = MathHelper.Clamp((heldTime - 2) / 8, 0, 1);
                            ApplyGripDamage(target, "oxygenlow", MathHelper.Lerp(2, 8, progress) * elapsed, null);
                            ApplyGripDamage(target, "explosiondamage", MathHelper.Lerp(0.25f, 1.5f, progress) * elapsed, grabbed);
                        }
                    }
                }
            }
            grabbedBodies.Clear();
            if (target.AnimController.Collider != null) grabbedBodies.Add(target.AnimController.Collider);
            foreach (var limb in target.AnimController.Limbs)
                if (!limb.Removed && !limb.IsSevered && limb.body != null) grabbedBodies.Add(limb.body);
            foreach (var body in grabbedBodies)
            {
                if (!body.Enabled) continue;
                Vector2 acceleration = (velocity - body.LinearVelocity) * 12;
                if (acceleration.LengthSquared() > 3600) acceleration = Vector2.Normalize(acceleration) * 60;
                body.ApplyForce((acceleration - GameMain.World.Gravity) * body.Mass);
            }
            grabbedBodies.Clear();
        }
#if SERVER
        private void WriteCombat(IWriteMessage msg)
        {
            msg.WriteBoolean(AttackMode);
            WriteInterference(msg);
            msg.WriteUInt16(grabbed?.character.ID ?? (ushort)0);
            msg.WriteInt32(grabbed == null ? -1 : Array.IndexOf(grabbed.character.AnimController.Limbs, grabbed));
            foreach (var arm in arms)
            {
                msg.WriteByte(arm.Phase); msg.WriteUInt16(arm.Sub?.ID ?? (ushort)0);
                msg.WriteSingle(arm.Start.X); msg.WriteSingle(arm.Start.Y);
                msg.WriteSingle(arm.End.X); msg.WriteSingle(arm.End.Y);
                msg.WriteSingle((float)(Timing.TotalTime - arm.Born)); msg.WriteSingle(arm.Duration);
                msg.WriteBoolean(arm.Powered);
            }
        }
        public void ServerEventRead(IReadMessage msg, Client client)
        {
            byte kind = msg.ReadByte(); Vector2 cursor = new(msg.ReadSingle(), msg.ReadSingle());
            RequestCombat(client?.Character, kind, cursor);
            if (kind == 0 && arms[0].Phase == 1 && grabOwner == client?.Character)
                grabClient = client;
        }
#endif
#if CLIENT
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
        {
            var request = extraData is Item.ComponentStateEventData data && data.ComponentData is CombatRequest r ? r : new CombatRequest(255, Vector2.Zero);
            msg.WriteByte(request.Kind); msg.WriteSingle(request.Cursor.X); msg.WriteSingle(request.Cursor.Y);
        }
        private void ReadCombat(IReadMessage msg)
        {
            AttackMode = msg.ReadBoolean();
            ReadInterference(msg);
            var character = Entity.FindEntityByID(msg.ReadUInt16()) as Character;
            int index = msg.ReadInt32();
            grabbed = character != null && index >= 0 && index < character.AnimController.Limbs.Length ? character.AnimController.Limbs[index] : null;
            foreach (var arm in arms)
            {
                arm.Phase = msg.ReadByte(); arm.Sub = Entity.FindEntityByID(msg.ReadUInt16()) as Submarine;
                arm.Start = new Vector2(msg.ReadSingle(), msg.ReadSingle()); arm.End = new Vector2(msg.ReadSingle(), msg.ReadSingle());
                arm.Born = Timing.TotalTime - msg.ReadSingle(); arm.Duration = msg.ReadSingle();
                arm.Powered = msg.ReadBoolean();
            }
        }
        private Sprite BladeSprite(int index) => (arms[index].Phase != 0 ? arms[index].Powered : CombatBattery() != null) ? blueBlade : blade;
        private bool CombatTip(int index, out Vector2 point)
        {
            var arm = arms[index]; point = default;
            if (arm.Phase == 0) return false;
            if (index == 0 && arm.Phase == 3 && grabbed != null && !grabbed.Removed)
            { point = grabbed.DrawPosition; return true; }
            float age = (float)(Timing.TotalTime - arm.Born);
            point = Vector2.Lerp(arm.Start, arm.End, MathHelper.Clamp(age / Math.Max(0.01f, arm.Duration), 0, 1))
                + (arm.Sub?.DrawPosition ?? Vector2.Zero);
            return true;
        }
#endif
    }
}
