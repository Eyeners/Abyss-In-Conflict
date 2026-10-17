using System;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;

namespace AIC.Components
{
    partial class AICCloneSubstrate
    {
        internal static readonly string[] PollutionPool = { "tunnelvision", "rigidjoints", "hypersensitivity",
            "xenobiology", "outsideinfluence", "glassjaw", "decrepify", "musculardystrophy", "inflamedlung" };
        [Serialize("", IsPropertySaveable.Yes)] public string Pollution { get; set; }
        [Serialize(0.6f, IsPropertySaveable.No)] public float DeathPollutionChance { get; set; }

        internal static string[] ParsePollution(string value) => (value ?? "").Split(',')
            .Where(id => PollutionPool.Contains(id)).Distinct().ToArray();

        internal void Recover(string previous, bool dead)
        {
            var effects = ParsePollution(previous).ToList();
            var candidates = PollutionPool.Except(effects).ToArray();
            if (dead && candidates.Length > 0 && Rand.Range(0f, 1f) < DeathPollutionChance)
                effects.Add(candidates[Rand.Range(0, candidates.Length)]);
            Pollution = string.Join(",", effects);
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }

        internal static void ApplyPollution(Character character, string value)
        {
            foreach (var id in ParsePollution(value))
            {
                var prefab = AfflictionPrefab.Prefabs.Find(p => p.Identifier == id.ToIdentifier());
                if (prefab != null) character.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(prefab.MaxStrength));
            }
        }
#if CLIENT
        public override void AddTooltipInfo(ref LocalizedString name, ref LocalizedString description)
        {
            var effects = ParsePollution(Pollution);
            if (effects.Length == 0) return;
            name += " [" + TextManager.Get("aic.clone.pollution") + "]";
            description += "\n" + TextManager.Get("aic.clone.pollution") + ": "
                + string.Join(", ", effects.Select(id => TextManager.Get("afflictionname." + id).Value));
        }

        public void ClientEventRead(IReadMessage msg, float sendingTime) => Pollution = msg.ReadString();
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) { }
#endif
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
            => msg.WriteString(Pollution ?? "");
        public void ServerEventRead(IReadMessage msg, Client client) { }
#endif
    }
}
