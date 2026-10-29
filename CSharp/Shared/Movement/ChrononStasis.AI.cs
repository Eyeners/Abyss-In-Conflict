using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    internal sealed partial class ChrononStasis
    {
        private double aiHitUntil, aiShieldReady, aiFreezeReady, aiThink;
        private double aiRetreatCheck;
        private float aiWindup;
        private Vector2 aiPoint;
        private Submarine aiSub;
        private Character aiOwner;
        private Vector2 AIWorldPoint => aiPoint + (aiSub?.Position ?? Vector2.Zero);

        internal static void AIDamaged(CharacterHealth __instance, AttackResult attackResult)
        {
            var c = __instance.Character;
            if (GameMain.NetworkMember?.IsClient == true || !c.IsAIControlled || attackResult.Damage <= 0) return;
            var pack = c.Inventory?.GetItemInLimbSlot(InvSlotType.Bag)?.GetComponent<ChrononStasis>();
            if (pack != null)
            {
                if (pack.aiOwner == null) pack.aiOwner = c;
                pack.aiHitUntil = Timing.TotalTime + 1.5;
            }
        }
        private void CancelAIWindup()
        {
            if (aiWindup <= 0) return;
            aiWindup = 0;
#if CLIENT
            SonicRefraction.CancelNPCStasisPreview(Item.ID);
#endif
            Sync();
        }
        private void UpdateAI(float dt)
        {
            var c = Item.ParentInventory?.Owner as Character;
            if (c == null || c != aiOwner)
            {
                CancelAIWindup(); aiOwner = c; aiHitUntil = 0;
            }
            if (c?.IsAIControlled != true || c.Removed || c.IsDead || c.IsIncapacitated
                || !c.AllowInput || ChrononFreeze.Frozen(c) || Item.Condition <= 0
                || c.Inventory?.GetItemInLimbSlot(InvSlotType.Bag) != Item)
            { CancelAIWindup(); return; }

            if (Timing.TotalTime >= aiRetreatCheck)
            {
                aiRetreatCheck = Timing.TotalTime + 0.15;
                if (TryAIRetreat(c)) { CancelAIWindup(); return; }
            }

            if (aiWindup > 0)
            {
                if (aiSub?.Removed == true) { CancelAIWindup(); return; }
                aiWindup = Math.Max(0, aiWindup - dt);
                if (aiWindup == 0)
                {
#if CLIENT
                    SonicRefraction.CancelNPCStasisPreview(Item.ID);
#endif
                    if (Start(c, true, AIWorldPoint)) aiFreezeReady = Timing.TotalTime + 15;
                    else Sync();
                }
                return;
            }
            double now = Timing.TotalTime;
            if (now < aiThink) return;
            aiThink = now + 0.15;
            var combat = SpaceBackpackAI.ActiveCombat(c);
            bool ranged = combat?.WeaponComponent is RangedWeapon;
            if (combat?.WeaponComponent == null)
                foreach (var item in c.HeldItems) if (item.GetComponent<RangedWeapon>() != null) ranged = true;
            // Defensive activation does not need a visible attacker (e.g. shots from behind).
            if (ranged && now <= aiHitUntil && now >= aiShieldReady && Start(c, false, c.WorldPosition))
            { aiShieldReady = now + 8; aiHitUntil = 0; return; }
            var enemy = combat?.Enemy;
            if (combat == null || combat.Abandon || combat.IsCompleted
                || (combat.Mode != AIObjectiveCombat.CombatMode.Offensive && combat.Mode != AIObjectiveCombat.CombatMode.Defensive)
                || enemy == null || enemy.Removed || enemy.IsDead || c.IsOnFriendlyTeam(enemy)) return;
            Vector2 delta = enemy.WorldPosition - c.WorldPosition;
            if (delta.LengthSquared() > 1200 * 1200 || !c.CanSeeTarget(enemy)) return;
            if (now <= aiHitUntil)
            {
                if (!ranged && Math.Abs(delta.Y) < 180 && Math.Abs(delta.X) > 150)
                {
                    var dash = Item.GetComponent<ChrononPack>();
                    if (dash?.TryAIDash(c, Math.Sign(delta.X), (Math.Abs(delta.X) - 120) / 100f) == true)
                    { aiHitUntil = 0; return; }
                }
            }
            if (now < aiFreezeReady || remaining > 0 || fragments > 0
                || Item.GetComponent<ChrononPack>()?.Dashing == true || ChrononFreeze.Frozen(enemy)) return;
            aiSub = enemy.Submarine;
            aiPoint = enemy.WorldPosition - (aiSub?.Position ?? Vector2.Zero);
            aiWindup = 1;
#if CLIENT
            SonicRefraction.AddNPCStasisPreview(Item.ID, AIWorldPoint, ConvertRadius(), aiWindup);
#endif
            Sync();
        }
        private bool TryAIRetreat(Character c)
        {
            var combat = SpaceBackpackAI.ActiveCombat(c);
            var enemy = combat?.Enemy;
            if (combat == null || combat.Abandon || combat.IsCompleted
                || combat.Mode == AIObjectiveCombat.CombatMode.None
                || enemy == null || enemy.Removed || enemy.IsDead || enemy.IsIncapacitated
                || c.IsOnFriendlyTeam(enemy) || ChrononFreeze.Frozen(enemy)) return false;
            bool ranged = combat.WeaponComponent is RangedWeapon;
            foreach (var held in c.HeldItems) if (held.GetComponent<RangedWeapon>() != null) ranged = true;
            if (!ranged) return false;
            // Armed ranged opponents are handled by the shield; fists and creatures count as melee.
            foreach (var held in enemy.HeldItems)
                if (held.GetComponent<RangedWeapon>() != null) return false;
            Vector2 delta = enemy.WorldPosition - c.WorldPosition;
            if (delta.LengthSquared() > 300 * 300 || Math.Abs(delta.Y) > 150
                || !c.CanSeeTarget(enemy)) return false;
            int away = delta.X >= 0 ? -1 : 1;
            if (Item.GetComponent<ChrononPack>()?.TryAIDash(c, away, 6) != true) return false;
            // Leave native combat steering in charge after the dash, without retaining a stale approach vector.
            (c.AIController as HumanAIController)?.SteeringManager.Reset();
            return true;
        }
        private float ConvertRadius() => FarseerPhysics.ConvertUnits.ToDisplayUnits(Radius);
#if SERVER
        private void WriteAI(IWriteMessage msg)
        {
            msg.WriteSingle(aiWindup); msg.WriteUInt16(aiSub?.ID ?? 0);
            msg.WriteSingle(aiPoint.X); msg.WriteSingle(aiPoint.Y);
        }
#endif
#if CLIENT
        private void ReadAI(IReadMessage msg)
        {
            float previousWindup = aiWindup;
            Vector2 previousPoint = aiPoint;
            aiWindup = msg.ReadSingle(); aiSub = Entity.FindEntityByID(msg.ReadUInt16()) as Submarine;
            aiPoint = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            if (aiWindup <= 0) SonicRefraction.CancelNPCStasisPreview(Item.ID);
            else if (previousWindup <= 0 || previousPoint != aiPoint)
                SonicRefraction.AddNPCStasisPreview(Item.ID, AIWorldPoint, ConvertRadius(), aiWindup);
        }
#endif
    }
}
