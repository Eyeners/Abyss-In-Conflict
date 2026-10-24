using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using FarseerPhysics;

namespace AIC.Components
{
    internal sealed class ChishaShotgun : ItemComponent
    {
        [Serialize(3f, IsPropertySaveable.No)] public float FlameRange { get; set; }
        [Serialize(12f, IsPropertySaveable.No)] public float FlameSpread { get; set; }
        private InsertionReload reload;
        internal float Loading => (reload ??= Item.GetComponent<InsertionReload>())?.Remaining ?? 0;
        private readonly HashSet<Character> burned = new();
        private readonly Attack flameAttack;
        private Vector2[] reloadHandles;
        public ChishaShotgun(Item item, ContentXElement element) : base(item, element)
        {
            var xml = element.GetChildElement("Attack");
            if (xml != null) flameAttack = new Attack(xml, item.Prefab.Identifier.ToString(), item);
        }
        internal void ReloadPose(ref bool aim, ref Vector2 itemPos, ref float holdAngle, ref Vector2[] handlePos)
        {
            if (Loading <= 0) return;
            float progress = 1 - Loading / Math.Max(0.1f, reload.Duration);
            aim = false;
            itemPos = ConvertUnits.ToSimUnits(new Vector2(42, -12));
            holdAngle = MathHelper.ToRadians(progress < 0.8f ? 25 : -15);
            // Like the Kyushu reload: lower the weapon, move the support hand
            // toward the loading port twice, then return to its normal grip.
            if (reloadHandles == null || reloadHandles.Length != handlePos.Length)
                reloadHandles = new Vector2[handlePos.Length];
            Array.Copy(handlePos, reloadHandles, handlePos.Length);
            handlePos = reloadHandles;
            float reach = (float)Math.Sin(Math.Clamp(progress / 0.8f, 0, 1) * MathHelper.TwoPi * 2);
            if (handlePos.Length > 1)
                handlePos[1] = ConvertUnits.ToSimUnits(new Vector2(-200 + 100 * reach, -45) * Item.Scale);
        }
        internal void Fired(RangedWeapon weapon, Character user)
        {
            if (GameMain.NetworkMember?.IsClient == true || user == null || flameAttack == null) return;
            float angle = Item.body.Rotation + (Item.body.Dir < 0 ? MathHelper.Pi : 0);
            Vector2 origin = Item.SimPosition + weapon.TransformedBarrelPos;
            // Do not let a muzzle protruding through a wall burn the far side.
            bool obstructed = false;
            if (Vector2.DistanceSquared(user.AnimController.AimSourceSimPos, origin) > 0.000001f)
            GameMain.World.RayCast((fixture, point, normal, fraction) =>
            {
                if (!fixture.IsSensor && Wall(fixture, user.Submarine)) { obstructed = true; return 0; }
                return -1;
            }, user.AnimController.AimSourceSimPos, origin);
            if (obstructed) return;
            burned.Clear();
            for (int ray = 0; ray < 9; ray++)
            {
                float a = angle + MathHelper.ToRadians(FlameSpread) * (ray / 4f - 1);
                Vector2 end = origin + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * Math.Max(0.1f, FlameRange);
                Limb nearest = null;
                GameMain.World.RayCast((fixture, point, normal, fraction) =>
                {
                    if (fixture.IsSensor) return -1;
                    if (fixture.Body.UserData is Limb limb)
                    {
                        if (limb.character == user || limb.Removed || limb.IsSevered || limb.character.Removed
                            || limb.character.Submarine != user.Submarine) return -1;
                        nearest = limb;
                        return fraction;
                    }
                    if (!Wall(fixture, user.Submarine)) return -1;
                    nearest = null;
                    return fraction;
                }, origin, end);
                if (nearest != null && burned.Add(nearest.character))
                    flameAttack.DoDamageToLimb(user, nearest, nearest.WorldPosition, 1, playSound: true);
            }
            burned.Clear();
        }
        private static bool Wall(FarseerPhysics.Dynamics.Fixture fixture, Submarine sub) =>
            fixture.Body.UserData is Structure s && s.Submarine == sub && !s.IsPlatform
            || fixture.Body.UserData is Item i && i.Submarine == sub && i.GetComponent<Door>() != null
            || sub == null && (fixture.CollisionCategories & Physics.CollisionLevel) != 0;
    }
    public sealed class ChishaShotgunHooks : IAssemblyPlugin
    {
        private Harmony harmony;
        private static void AfterUse(RangedWeapon __instance, Character character, bool __result)
        {
            if (__result && __instance.LastProjectile != null)
                __instance.Item.GetComponent<ChishaShotgun>()?.Fired(__instance, character);
        }
        private static void Pose(Item item, ref bool aim, ref Vector2 itemPos, ref float holdAngle, ref Vector2[] handlePos)
            => item.GetComponent<ChishaShotgun>()?.ReloadPose(ref aim, ref itemPos, ref holdAngle, ref handlePos);
        public void Initialize()
        {
            harmony = new Harmony("aic.chisha.shotgun");
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), "Use"),
                postfix: new HarmonyMethod(typeof(ChishaShotgunHooks), nameof(AfterUse)));
            harmony.Patch(AccessTools.Method(typeof(AnimController), "HoldItem"),
                prefix: new HarmonyMethod(typeof(ChishaShotgunHooks), nameof(Pose)));
        }
        public void Dispose() => harmony?.UnpatchSelf();
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
