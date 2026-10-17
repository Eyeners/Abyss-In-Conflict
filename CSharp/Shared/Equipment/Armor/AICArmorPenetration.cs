using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class AICArmorPenetration : IAssemblyPlugin
    {
        private sealed record Settings(float Multiplier, float Offset, bool HasMultiplier, bool HasOffset);
        private static ConditionalWeakTable<DamageModifier, Settings> settings = new();
        private Harmony harmony;
        private static readonly Identifier Heat = "aic_heat_buildup".ToIdentifier();
        private static readonly Identifier Breaker = "aic_armorbreaker".ToIdentifier();
        private static readonly Identifier Breaker2 = "aic_armorbreaker2".ToIdentifier();

        private static Vector3 ReadOffsets(Limb limb)
        {
            var health = limb.character?.CharacterHealth;
            if (health == null) return Vector3.Zero;
            return CalculateOffsets(health.GetAfflictionStrengthByIdentifier(Heat),
                health.GetAfflictionStrengthByIdentifier(Breaker), health.GetAfflictionStrengthByIdentifier(Breaker2));
        }

        private static Vector3 CalculateOffsets(float heat, float breaker, float breaker2)
        {
            float physical = heat <= 30 ? 0 : heat <= 60
                ? MathHelper.Lerp(0.1f, 0.15f, (heat - 30) / 30)
                : MathHelper.Lerp(0.15f, 0.2f, MathHelper.Clamp((heat - 60) / 39, 0, 1));
            float burn = heat <= 0 ? 0 : heat <= 30 ? 0.05f : heat <= 60 ? 0.1f : 0.2f;
            float broken = breaker <= 0 ? 0 : breaker <= 20 ? 0.1f : breaker <= 40 ? 0.15f : 0.2f;
            broken += breaker2 <= 0 ? 0 : breaker2 <= 20 ? 0.2f : breaker2 <= 40 ? 0.25f : 0.3f;
            return new Vector3(physical, burn, broken);
        }

        public void PreInitPatching() => Initialize();
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.armor.penetration");
            try
            {
                // Register the constructed object as well: callers compiled before our
                // Deserialize patch may already contain an inlined copy of that method.
                harmony.Patch(AccessTools.Constructor(typeof(DamageModifier), new[]
                    { typeof(ContentXElement), typeof(string), typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(AICArmorPenetration), nameof(ReadConstructed)));
                harmony.Patch(AccessTools.Method(typeof(DamageModifier), nameof(DamageModifier.Deserialize)),
                    postfix: new HarmonyMethod(typeof(AICArmorPenetration), nameof(Read)));
                harmony.Patch(AccessTools.Method(typeof(DamageModifier), nameof(DamageModifier.Serialize)),
                    postfix: new HarmonyMethod(typeof(AICArmorPenetration), nameof(Write)));
                harmony.Patch(AccessTools.Method(typeof(Affliction), nameof(Affliction.GetResistance)),
                    postfix: new HarmonyMethod(typeof(AICArmorPenetration), nameof(AdjustHeatResistance)));
                harmony.Patch(AccessTools.Method(typeof(Limb), nameof(Limb.AddDamage), new[]
                    { typeof(Vector2), typeof(IEnumerable<Affliction>), typeof(bool), typeof(float), typeof(float), typeof(Character) }),
                    transpiler: new HarmonyMethod(typeof(AICArmorPenetration), nameof(Inject)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                settings = new();
                LuaCsLogger.LogError("[AIC ArmorPenetration] " + error);
            }
        }

        public void OnLoadCompleted() { }
        private static void AdjustHeatResistance(Affliction __instance, Identifier __0, ref float __result)
        {
            // Native afflictions select one active Effect: overlapping XML Effects cannot
            // supply separate physical and thermal resistance curves for the same tier.
            if (__result >= 0 || __instance.Identifier != Heat) return;
            var incoming = AfflictionPrefab.Prefabs[__0];
            if (incoming.AfflictionType == "damage".ToIdentifier()
                && __0 != "burn".ToIdentifier() && __0 != "acidburn".ToIdentifier())
                __result *= 0.5f;
        }
        public void Dispose()
        {
            harmony?.UnpatchSelf(); harmony = null;
            settings = new();
        }

        private static void ReadConstructed(DamageModifier __instance, ContentXElement element)
        {
            if (!settings.TryGetValue(__instance, out _)) Read(__instance, element);
        }

        private static void Read(DamageModifier __instance, XElement element)
        {
            if (element == null) return;
            settings.Remove(__instance);
            var multiplier = element.GetAttribute("penetrationmultiplier");
            var offset = element.GetAttribute("penetrationoffset");
            if (multiplier == null && offset == null) return;
            settings.Add(__instance, new Settings(Parse(multiplier, 1), Parse(offset, 0),
                multiplier != null, offset != null));
        }

        private static float Parse(XAttribute attribute, float fallback)
        {
            if (attribute == null) return fallback;
            if (float.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                && float.IsFinite(value)) return value;
            LuaCsLogger.LogError("[AIC ArmorPenetration] Invalid " + attribute.Name + "=" + attribute.Value);
            return fallback;
        }

        private static void Write(DamageModifier __instance, XElement element)
        {
            if (element == null || !settings.TryGetValue(__instance, out var value)) return;
            if (value.HasMultiplier) element.SetAttributeValue("penetrationmultiplier", value.Multiplier.ToString(CultureInfo.InvariantCulture));
            if (value.HasOffset) element.SetAttributeValue("penetrationoffset", value.Offset.ToString(CultureInfo.InvariantCulture));
        }

        private static float Interpolate(float damage, float unarmored, float penetration, DamageModifier modifier,
            Affliction affliction, Vector3 offsets)
        {
            // Each layer receives the same input, including any earlier VCE adjustment.
            // Unconfigured modifiers retain vanilla extrapolation outside 0..1.
            float offset = affliction.Prefab.AfflictionType == "damage".ToIdentifier() ? offsets.X
                : affliction.Prefab.AfflictionType == "burn".ToIdentifier() ? offsets.Y : 0;
            var id = affliction.Identifier;
            if (id == "gunshotwound".ToIdentifier() || id == "lacerations".ToIdentifier() || id == "blunttrauma".ToIdentifier())
                offset += offsets.Z;
            bool configured = settings.TryGetValue(modifier, out var value);
            if (configured) penetration = penetration * value.Multiplier + value.Offset;
            if (configured || offset != 0) penetration = MathHelper.Clamp(penetration + offset, 0, 1);
            return MathHelper.Lerp(damage, unarmored, penetration);
        }

        private static IEnumerable<CodeInstruction> Inject(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = instructions.ToList();
            var matches = AccessTools.Method(typeof(DamageModifier), nameof(DamageModifier.MatchesAffliction), new[] { typeof(Affliction) });
            int matchIndex = code.FindIndex(c => c.Calls(matches));
            if (matchIndex < 1 || !code[matchIndex - 1].opcode.Name.StartsWith("ldloc"))
                throw new InvalidOperationException("Cannot locate native incoming affliction local.");
            var afflictionLoad = new CodeInstruction(code[matchIndex - 1].opcode, code[matchIndex - 1].operand);
            var offsets = generator.DeclareLocal(typeof(Vector3));
            var getter = AccessTools.PropertyGetter(typeof(DamageModifier), nameof(DamageModifier.DamageMultiplier));
            var lerp = AccessTools.Method(typeof(MathHelper), nameof(MathHelper.Lerp), new[] { typeof(float), typeof(float), typeof(float) });
            int start = code.FindIndex(c => c.Calls(getter));
            int index = start < 0 ? -1 : code.FindIndex(start, c => c.Calls(lerp));
            if (start < 1 || index < 0 || index - start > 30
                || !code[start - 1].opcode.Name.StartsWith("ldloc"))
                throw new InvalidOperationException("Cannot locate native per-modifier armor penetration interpolation.");
            var load = new CodeInstruction(code[start - 1].opcode, code[start - 1].operand);
            load.labels.AddRange(code[index].labels);
            load.blocks.AddRange(code[index].blocks);
            code[index] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AICArmorPenetration), nameof(Interpolate)));
            code.Insert(index, load);
            code.Insert(index + 1, afflictionLoad);
            code.Insert(index + 2, new CodeInstruction(OpCodes.Ldloc, offsets));
            // Invocation-local snapshot: no polling, shared prefab mutation or nested-hit state.
            code.InsertRange(0, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AICArmorPenetration), nameof(ReadOffsets))),
                new CodeInstruction(OpCodes.Stloc, offsets)
            });
            return code;
        }

    }
}
