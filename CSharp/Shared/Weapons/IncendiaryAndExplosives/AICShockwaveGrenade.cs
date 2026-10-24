using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Collision;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
#if CLIENT
using Barotrauma.Particles;
#endif

namespace AIC.Components
{
    class AICShockwaveGrenade : ItemComponent, IServerSerializable
    {
        [Serialize(600f, IsPropertySaveable.No)] public float WaveRange { get; set; }
        [Serialize(200f, IsPropertySaveable.No)] public float WaveSpeed { get; set; }
        [Serialize("Both", IsPropertySaveable.No)] public string WaveDirection { get; set; }
        [Serialize(3f, IsPropertySaveable.No)] public float CameraShake { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool DebugWave { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool DistortionVisual { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float DistortionScale { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float DistortionStrength { get; set; }
        internal void StartAt(Character user, int direction)
        {
            waveSub = Item.Submarine; attacker = user; projectileDirection = direction;
            if (!Ground(Item.SimPosition + Vector2.UnitY * 0.5f, 1.7f, out var floor)) { Finish(); return; }
            BeginWave(floor, direction);
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        private int pulseCount, particlesCreated;
        private readonly Attack waveAttack;
        [Serialize(30f, IsPropertySaveable.No)] public float HorizontalImpulse { get; set; }
        [Serialize(8f, IsPropertySaveable.No)] public float UpwardImpulse { get; set; }
        [Serialize(2f, IsPropertySaveable.No)] public float MaxVelocityChange { get; set; }
        [Serialize(6f, IsPropertySaveable.No)] public float MaxPushSpeed { get; set; }
        private readonly HashSet<Body> pushedBodies = new();
        private Vector2 origin;
        private readonly HashSet<Character> hit = new();
        private readonly Dictionary<Character, Limb> candidates = new();
        private readonly Vector2[] fronts = new Vector2[2];
        private readonly bool[] alive = new bool[2];
        private bool started, finished;
        private float elapsed, distance;
        private int projectileDirection = 1;
        private Submarine waveSub;
        private Character attacker;
        private const float Step = 25f;
        public AICShockwaveGrenade(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
            if (DebugWave) DebugConsole.NewMessage("[AIC Shockwave] Component loaded v2; item=" + item.ID);
            foreach (var child in element.GetChildElements("attack"))
            {
                waveAttack = new Attack(child, item.Prefab.Identifier.ToString(), item);
                break;
            }
        }
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        public override void Update(float deltaTime, Camera cam)
        {
            if (started) { UpdateBroken(deltaTime, cam); return; }
            // Remember flight direction before impact changes or stops the projectile velocity.
            float velocityX = Item.body?.LinearVelocity.X ?? 0;
            if (float.IsFinite(velocityX) && Math.Abs(velocityX) > 0.01f)
                projectileDirection = velocityX < 0 ? -1 : 1;
        }
        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            if (finished || Item.Removed) return;
            if (!started)
            {
                if (!Authority) return;
                waveSub = Item.Submarine;
                attacker = Item.GetComponent<Projectile>()?.Attacker;
                // Impact resolution can leave the projectile center inside the floor.
                if (!Ground(Item.SimPosition + Vector2.UnitY * 0.5f, 1.7f, out var ground))
                {
                    if (DebugWave) DebugConsole.NewMessage("[AIC Shockwave] Impact reached; initial ground query failed.");
                    Finish(); return;
                }
                int direction = ResolveDirection();
                BeginWave(ground, direction);
#if SERVER
                GameMain.Server?.CreateEntityEvent(Item, new Item.ComponentStateEventData(this, null));
#endif
            }
            elapsed += deltaTime;
            float target = Math.Min(WaveRange, elapsed * Math.Max(1, WaveSpeed));
            // Fixed spatial steps prevent gaps at low frame rates.
            while (distance + Step <= target)
            {
                distance += Step;
                for (int side = 0; side < 2; side++)
                {
                    if (!alive[side]) continue;
                    var previous = fronts[side];
                    var next = previous + new Vector2(ConvertUnits.ToSimUnits(Step) * (side == 0 ? -1 : 1), 0);
                    var blocker = Submarine.PickBody(previous + Vector2.UnitY * 0.12f, next + Vector2.UnitY * 0.12f,
                        collisionCategory: Physics.CollisionWall | Physics.CollisionLevel,
                        customPredicate: Surface, allowInsideFixture: true);
                    bool hasFloor = Ground(next + Vector2.UnitY * 0.3f, 0.65f, out var floor);
                    if (blocker != null || !hasFloor)
                    {
                        if (DebugWave) DebugConsole.NewMessage("[AIC Shockwave] Side stopped=" + side
                            + "; blocker=" + (blocker?.UserData?.GetType().Name ?? "none")
                            + "; floor=" + hasFloor + "; distance=" + distance);
                        alive[side] = false; continue;
                    }
                    fronts[side] = floor;
                    Pulse(previous, floor);
                }
            }
            if ((!alive[0] && !alive[1]) || target >= WaveRange) Finish();
        }
        private bool Surface(Fixture fixture)
        {
            if (fixture.IsSensor) return false;
            if (fixture.Body.UserData is Structure structure) return structure.Submarine == waveSub;
            if (fixture.Body.UserData is Item item) return item.Submarine == waveSub && item.GetComponent<Door>() != null;
            return waveSub == null && (fixture.CollisionCategories & Physics.CollisionLevel) != 0;
        }
        private int ResolveDirection()
        {
            string mode = WaveDirection?.Trim();
            if (string.Equals(mode, "Left", StringComparison.OrdinalIgnoreCase)) return -1;
            if (string.Equals(mode, "Right", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(mode, "ProjectileDirection", StringComparison.OrdinalIgnoreCase)) return projectileDirection;
            if (string.Equals(mode, "AwayFromAttacker", StringComparison.OrdinalIgnoreCase))
            {
                if (attacker != null && !attacker.Removed)
                {
                    float offset = Item.WorldPosition.X - attacker.WorldPosition.X;
                    if (float.IsFinite(offset) && Math.Abs(offset) > 5f) return offset < 0 ? -1 : 1;
                }
                return projectileDirection;
            }
            // Both (also the fallback for an unrecognized value).
            return 0;
        }
        private bool Ground(Vector2 start, float depth, out Vector2 position)
        {
            var body = Submarine.PickBody(start, start - Vector2.UnitY * depth,
                collisionCategory: Physics.CollisionWall | Physics.CollisionPlatform | Physics.CollisionLevel,
                customPredicate: Surface, allowInsideFixture: false);
            position = Submarine.LastPickedPosition;
            return body != null && Submarine.LastPickedNormal.Y > 0.5f;
        }
        private void Pulse(Vector2 from, Vector2 to)
        {
            pulseCount++;
            // Physics coordinates already include HiddenSubPosition. Match Entity.WorldPosition:
            // add Submarine.Position, not WorldPosition (which would add the hidden offset twice).
            Vector2 world = ConvertUnits.ToDisplayUnits(to) + (waveSub?.Position ?? Vector2.Zero);
#if CLIENT
            Vector2 visualPosition = world + Vector2.UnitY * 18;
            var visualHull = Hull.FindHull(visualPosition);
            if (DistortionVisual)
            {
                if (Vector2.DistanceSquared(from, to) > 0.000001f)
                    SonicRefraction.AddGroundWave(ConvertUnits.ToDisplayUnits(from) + (waveSub?.Position ?? Vector2.Zero) - Vector2.UnitY * 10,
                        world - Vector2.UnitY * 10, Step / Math.Max(1, WaveSpeed), DistortionScale, DistortionStrength,
                        new EffectAnchor(waveSub, waveSub?.Position ?? Vector2.Zero));
            }
            else
            {
                EmitWaveParticle("aic_groundwave_dust", visualPosition, Vector2.UnitY * 45, visualHull);
                EmitWaveParticle("aic_groundwave_ring", visualPosition, Vector2.Zero, visualHull);
            }
#endif
            candidates.Clear();
            Vector2 lower = new Vector2(Math.Min(from.X, to.X) - 0.15f, Math.Min(from.Y, to.Y) - 0.08f);
            Vector2 upper = new Vector2(Math.Max(from.X, to.X) + 0.15f, Math.Max(from.Y, to.Y) + 0.6f);
            var bounds = new AABB(lower, upper);
            GameMain.World.QueryAABB(fixture =>
            {
                if (fixture.Body.UserData is Limb limb && limb.character is Character character
                    && !limb.Removed && !limb.IsSevered
                    && !character.Removed && character.Submarine == waveSub && !hit.Contains(character))
                {
#if CLIENT
                    // Remote clients only query their controlled character for local feedback.
                    if (!Authority && character != Character.Controlled) return true;
#endif
                    // Broad-phase proxies are padded: reject distant limb centers.
                    Vector2 pos = limb.SimPosition;
                    if (pos.X >= lower.X && pos.X <= upper.X && pos.Y >= lower.Y && pos.Y <= upper.Y)
                        candidates.TryAdd(character, limb);
                }
                return true;
            }, ref bounds);
            foreach (var pair in candidates)
            {
                hit.Add(pair.Key);
#if CLIENT
                if (pair.Key == Character.Controlled && Screen.Selected?.Cam is Camera camera)
                    camera.Shake = Math.Max(camera.Shake, Math.Max(0, CameraShake));
#endif
                if (!Authority) continue;
                // Corpses still have physical ragdoll limbs, but need no living affliction effects.
                if (!pair.Key.IsDead)
                    waveAttack?.DoDamageToLimb(attacker, pair.Value, pair.Value.WorldPosition, 1f, playSound: true);
                Push(pair.Key, to);
            }
        }
        private int initialDirection;
#if CLIENT
        private void EmitWaveParticle(string id, Vector2 position, Vector2 velocity, Hull hull)
        {
            var prefab = ParticleManager.FindPrefab(id);
            // The string overload always supplies Default, regardless of XML draworder.
            var particle = GameMain.ParticleManager?.CreateParticle(prefab, position, velocity,
                hullGuess: hull, drawOrder: ParticleDrawOrder.Foreground);
            if (particle != null)
            {
                particlesCreated++;
                if (DebugWave && pulseCount == 1) DebugConsole.NewMessage("[AIC Shockwave] Particle created: " + id + "; world=" + position);
            }
            else if (DebugWave && pulseCount == 1)
                DebugConsole.NewMessage("[AIC Shockwave] Particle creation failed: " + id
                    + "; prefab=" + (prefab != null) + "; world=" + position);
        }
#endif
        private void BeginWave(Vector2 ground, int direction)
        {
            if (DebugWave) DebugConsole.NewMessage("[AIC Shockwave] Begin; authority=" + Authority
                + "; sub=" + (waveSub?.ID ?? 0) + "; origin=" + ground + "; direction=" + direction);
            started = true;
            initialDirection = direction;
            fronts[0] = fronts[1] = origin = ground;
            alive[0] = direction <= 0;
            alive[1] = direction >= 0;
            if (Item.body != null) Item.body.Enabled = false;
            Pulse(ground, ground);
        }
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteUInt16(waveSub?.ID ?? (ushort)0);
            msg.WriteSingle(origin.X);
            msg.WriteSingle(origin.Y);
            msg.WriteInt32(initialDirection);
        }
#endif
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            ushort subId = msg.ReadUInt16();
            Vector2 ground = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            int direction = msg.ReadInt32();
            if (started || finished) return;
            waveSub = Entity.FindEntityByID(subId) as Submarine;
            BeginWave(ground, direction);
        }
#endif
        private void Push(Character character, Vector2 wavePosition)
        {
            var anim = character.AnimController;
            if (anim == null) return;
            pushedBodies.Clear();
            float mass = 0;
            void Include(Body body)
            {
                if (body == null || !body.Enabled || body.BodyType != BodyType.Dynamic
                    || !float.IsFinite(body.Mass) || body.Mass <= 0 || !pushedBodies.Add(body)) return;
                mass += body.Mass;
            }
            Include(anim.Collider?.FarseerBody);
            foreach (var limb in anim.Limbs)
                if (!limb.Removed && !limb.IsSevered) Include(limb.body?.FarseerBody);
            if (mass <= 0 || !float.IsFinite(mass)) return;
            float direction = Math.Sign(wavePosition.X - origin.X);
            if (direction == 0) direction = character.SimPosition.X < origin.X ? -1 : 1;
            Vector2 change = new Vector2(direction * Math.Max(0, HorizontalImpulse), Math.Max(0, UpwardImpulse)) / mass;
            float length = change.Length();
            if (!float.IsFinite(length) || length <= 0) return;
            if (length > Math.Max(0, MaxVelocityChange)) change *= Math.Max(0, MaxVelocityChange) / length;
            // A common velocity change preserves joint motion; apply at each center of mass, without torque.
            float scale = 1;
            foreach (var body in pushedBodies)
            {
                if (change.X != 0)
                    scale = Math.Min(scale, Math.Max(0, MaxPushSpeed - body.LinearVelocity.X * direction) / Math.Abs(change.X));
                if (change.Y > 0)
                    scale = Math.Min(scale, Math.Max(0, MaxPushSpeed - body.LinearVelocity.Y) / change.Y);
            }
            foreach (var body in pushedBodies)
                body.ApplyLinearImpulse(change * scale * body.Mass);
            pushedBodies.Clear();
        }
        private void Finish()
        {
            finished = true;
            if (DebugWave) DebugConsole.NewMessage("[AIC Shockwave] Finished; pulses=" + pulseCount
                + "; client particles=" + particlesCreated + "; distance=" + distance);
            if (Authority) Entity.Spawner?.AddItemToRemoveQueue(Item);
        }
    }
}
