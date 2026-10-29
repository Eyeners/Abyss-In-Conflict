using System;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    partial class MagneticChainPack
    {
        private bool aiGrab;
        private Character aiCombatTarget;
        private double nextAICombat, nextAIGrab;
        private double nextHoldCheck;
        private Vector2 holdOffset;
        private Limb holdLimb;
        private void UpdateAICombat(Character c)
        {
            if (!c.IsAIControlled) { aiCombatTarget = null; return; }
            var combat = SpaceBackpackAI.ActiveCombat(c);
            var enemy = combat?.Enemy;
            bool attacking = combat != null && !combat.Abandon && !combat.IsCompleted
                && (combat.Mode == AIObjectiveCombat.CombatMode.Offensive || combat.Mode == AIObjectiveCombat.CombatMode.Defensive);
            if (!attacking || enemy == null || enemy.Removed || enemy.IsDead || c.IsOnFriendlyTeam(enemy)
                || Vector2.DistanceSquared(c.WorldPosition, enemy.WorldPosition) > AttackReach * AttackReach)
            {
                aiCombatTarget = null;
                ReleaseAIGrab(c);
                return;
            }
            if (aiGrab && aiCombatTarget != enemy) ReleaseAIGrab(c);
            aiCombatTarget = enemy;
            if (Timing.TotalTime < nextAICombat) return;
            nextAICombat = Timing.TotalTime + 0.15;
            Vector2 aim = (grabbed?.character == enemy ? grabbed : enemy.AnimController.MainLimb)?.WorldPosition ?? enemy.WorldPosition;
            if (Sweep(c, c.WorldPosition, aim, out _, wallsOnly: true, passPlatforms: true) != null)
            { ReleaseAIGrab(c); return; }
            // Use the existing perceived combat target; leave native aiming and held-item input untouched.
            var first = Sweep(c, c.WorldPosition, aim, out _, passPlatforms: true);
            if (first?.Body.UserData is Limb blocking && blocking.character != enemy) return;
            if (arms[0].Phase == 0 && Timing.TotalTime >= nextAIGrab
                && Timing.TotalTime >= nextGrab && Timing.TotalTime >= armDisabledUntil[0])
            {
                nextAIGrab = Timing.TotalTime + 3;
                nextGrab = Timing.TotalTime + 0.3;
                StartArm(0, c, aim);
            }
            if (Timing.TotalTime < nextStab) return;
            if (Timing.TotalTime < armDisabledUntil[nextBlade]) nextBlade = nextBlade == 1 ? 2 : 1;
            if (arms[nextBlade].Phase != 0 || Timing.TotalTime < armDisabledUntil[nextBlade]) return;
            nextStab = Timing.TotalTime + 0.6;
            StartArm(nextBlade, c, aim);
            nextBlade = nextBlade == 1 ? 2 : 1;
        }
        private void ReleaseAIGrab(Character c)
        {
            if (!aiGrab || arms[0].Phase == 0 || arms[0].Phase == 2) return;
            ReturnArm(0, c, grabbed?.WorldPosition ?? arms[0].Position((float)(Timing.TotalTime - arms[0].Born)));
        }
        private Vector2 AIHoldPosition(Character c)
        {
            if (grabbed == null) return c.WorldPosition;
            if (holdLimb == grabbed && Timing.TotalTime < nextHoldCheck) return c.WorldPosition + holdOffset;
            holdLimb = grabbed;
            nextHoldCheck = Timing.TotalTime + 0.15;
            Vector2 result = FindAIHoldPosition(c);
            holdOffset = result - c.WorldPosition;
            return result;
        }
        private Vector2 FindAIHoldPosition(Character c)
        {
            float sign = grabbed.WorldPosition.X >= c.WorldPosition.X ? 1 : -1;
            // Prefer 2.5 m ahead, then shorter/higher alternatives. Do not pull through walls or into the wearer.
            for (int i = 0; i < 4; i++)
            {
                Vector2 candidate = c.WorldPosition + new Vector2(sign * (i < 2 ? 250 : 200), i % 2 == 0 ? 35 : 100);
                bool clear = true;
                for (int j = -1; j <= 1; j++)
                {
                    Vector2 margin = new Vector2(0, j * 45);
                    if (Sweep(c, c.WorldPosition + margin, candidate + margin, out _, wallsOnly: true, passPlatforms: true) != null
                        || Sweep(c, grabbed.WorldPosition + margin, candidate + margin, out _, wallsOnly: true, passPlatforms: true) != null)
                    { clear = false; break; }
                }
                if (clear) return candidate;
            }
            return grabbed.WorldPosition;
        }
    }
}
