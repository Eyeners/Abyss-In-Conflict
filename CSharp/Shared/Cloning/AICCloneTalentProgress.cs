using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Abilities;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    // Mirror state, never replay an ability event: physical rewards must be issued once.
    public sealed class AICCloneTalentProgress : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool mirroring;
        private static ConditionalWeakTable<TalentPrefab, HashSet<Identifier>> statKeys = new();
        private sealed class Pair { internal CharacterAbilityGroup Other; }
        private static ConditionalWeakTable<CharacterAbilityGroup, Pair> pairs = new();

        public void PreInitPatching() => Initialize();
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.clone.talentprogress");
            harmony.Patch(AccessTools.Method(typeof(CharacterInfo), nameof(CharacterInfo.ChangeSavedStatValue)),
                prefix: new HarmonyMethod(typeof(AICCloneTalentProgress), nameof(BeforeStat)),
                postfix: new HarmonyMethod(typeof(AICCloneTalentProgress), nameof(AfterStat)));
            harmony.Patch(AccessTools.Method(typeof(CharacterInfo), nameof(CharacterInfo.ResetSavedStatValue)),
                prefix: new HarmonyMethod(typeof(AICCloneTalentProgress), nameof(BeforeStat)),
                postfix: new HarmonyMethod(typeof(AICCloneTalentProgress), nameof(AfterReset)));
            harmony.Patch(AccessTools.Method(typeof(CharacterAbilityGroupEffect), nameof(CharacterAbilityGroupEffect.CheckAbilityGroup)),
                prefix: new HarmonyMethod(typeof(AICCloneTalentProgress), nameof(BeforeGroup)),
                postfix: new HarmonyMethod(typeof(AICCloneTalentProgress), nameof(AfterGroup)));
            harmony.Patch(AccessTools.Method(typeof(CharacterAbilityGroupInterval), nameof(CharacterAbilityGroupInterval.UpdateAbilityGroup)),
                prefix: new HarmonyMethod(typeof(AICCloneTalentProgress), nameof(BeforeGroup)),
                postfix: new HarmonyMethod(typeof(AICCloneTalentProgress), nameof(AfterGroup)));
        }

        private static AICCloneChamber Link(Character character)
        {
            if (character == null || GameMain.NetworkMember?.IsClient == true) return null;
            if (AICCloneChamber.Bodies.TryGetValue(character, out var chamber)
                || AICCloneChamber.Clones.TryGetValue(character, out chamber)) return chamber;
            return null;
        }

        private static Character Other(AICCloneChamber chamber, Character character)
            => character == chamber.Original ? chamber.Clone : chamber.Original;

        private static bool Active(AICCloneChamber chamber, Character character)
            => character == (chamber.Connected ? chamber.Clone : chamber.Original);

        private static bool IsTalentStat(Character character, Identifier key)
            => character.CharacterTalents.Any(t => statKeys.GetValue(t.Prefab, ReadKeys)
                .Any(pattern => Matches(key, pattern)));

        private static bool Matches(Identifier key, Identifier pattern)
        {
            if (key == pattern) return true;
            int wildcard = pattern.IndexOf('*');
            return wildcard >= 0 && key.StartsWith(pattern[0..wildcard]);
        }

        private static HashSet<Identifier> ReadKeys(TalentPrefab prefab)
        {
            XElement xml = prefab.ConfigElement;
            return xml.Descendants().Attributes("statidentifier")
                .Select(a => a.Value.ToIdentifier()).ToHashSet();
        }

        private static bool BeforeStat(CharacterInfo __instance, Identifier statIdentifier)
        {
            if (mirroring || AICCloneSystem.Syncing) return true;
            var chamber = Link(__instance.Character);
            return chamber == null || !IsTalentStat(__instance.Character, statIdentifier)
                || Active(chamber, __instance.Character);
        }

        private static void AfterStat(CharacterInfo __instance, StatTypes statType, Identifier statIdentifier)
        {
            if (mirroring || AICCloneSystem.Syncing) return;
            var chamber = Link(__instance.Character);
            if (chamber == null || !Active(chamber, __instance.Character)
                || !IsTalentStat(__instance.Character, statIdentifier)) return;
            var other = Other(chamber, __instance.Character);
            if (!AICCloneChamber.Alive(other)) return;
            if (__instance.SavedStatValues.TryGetValue(statType, out var values))
            {
                var value = values.FirstOrDefault(s => s.StatIdentifier == statIdentifier);
                if (value != null) CopyStat(other.Info, statType, value);
            }
        }

        private static void AfterReset(CharacterInfo __instance, Identifier statIdentifier)
        {
            // ResetSavedStatValue clears reward tokens across all stat types. Copy zeros too:
            // copying just the positive progress would allow the other body to claim again.
            if (mirroring || AICCloneSystem.Syncing) return;
            var chamber = Link(__instance.Character);
            if (chamber == null || !Active(chamber, __instance.Character)) return;
            foreach (var entry in __instance.SavedStatValues)
                foreach (var value in entry.Value.Where(v => Matches(v.StatIdentifier, statIdentifier)).ToArray())
                    AfterStat(__instance, entry.Key, value.StatIdentifier);
        }

        private static void CopyStat(CharacterInfo target, StatTypes type, SavedStatValue value)
        {
            mirroring = true;
            try
            {
                target.ChangeSavedStatValue(type, value.StatValue, value.StatIdentifier,
                    value.RemoveOnDeath, setValue: true);
            }
            finally { mirroring = false; }
        }

        internal static void Synchronize(Character original, Character clone)
        {
            if (GameMain.NetworkMember?.IsClient == true) return;
            foreach (var entry in original.Info.SavedStatValues)
                foreach (var value in entry.Value)
                    if (IsTalentStat(original, value.StatIdentifier)) CopyStat(clone.Info, entry.Key, value);
            foreach (var talent in original.CharacterTalents)
                foreach (var group in Groups(talent))
                {
                    var twin = FindTwin(group, clone);
                    if (twin != null) twin.timesTriggered = group.timesTriggered;
                }
        }

        private static IEnumerable<CharacterAbilityGroup> Groups(CharacterTalent talent)
            => talent.characterAbilityGroupEffectDictionary.Values.SelectMany(g => g).Cast<CharacterAbilityGroup>()
                .Concat(talent.characterAbilityGroupIntervals);

        private static CharacterAbilityGroup FindTwin(CharacterAbilityGroup group, Character other)
        {
            if (!AICCloneChamber.Alive(other)) return null;
            if (pairs.TryGetValue(group, out var pair) && pair.Other.Character == other
                && other.CharacterTalents.Contains(pair.Other.CharacterTalent)) return pair.Other;
            var talent = other.CharacterTalents.FirstOrDefault(t => t.Prefab.Identifier == group.CharacterTalent.Prefab.Identifier);
            if (talent == null) return null;
            bool Matches(CharacterAbilityGroup g) => g.GetType() == group.GetType() && g.AbilityEffectType == group.AbilityEffectType;
            int index = Groups(group.CharacterTalent).Where(Matches).TakeWhile(g => g != group).Count();
            var twin = Groups(talent).Where(Matches).ElementAtOrDefault(index);
            if (twin != null)
            {
                pairs.Remove(group);
                pairs.Add(group, new Pair { Other = twin });
            }
            return twin;
        }

        private static bool BeforeGroup(CharacterAbilityGroup __instance, out CharacterAbilityGroup __state)
        {
            __state = null;
            var chamber = Link(__instance.Character);
            if (chamber == null) return true;
            if (!Active(chamber, __instance.Character)) return false;
            if (__instance.maxTriggerCount == int.MaxValue) return true;
            __state = FindTwin(__instance, Other(chamber, __instance.Character));
            if (__state != null) __instance.timesTriggered = Math.Max(__instance.timesTriggered, __state.timesTriggered);
            return true;
        }

        private static void AfterGroup(CharacterAbilityGroup __instance, CharacterAbilityGroup __state)
        {
            if (__state != null) __state.timesTriggered = __instance.timesTriggered;
        }

        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf(); harmony = null;
            statKeys = new(); pairs = new(); mirroring = false;
        }
    }
}
