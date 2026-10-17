using System;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    partial class AICCloneChamber
    {
        private const string OwnerMarkerPrefix = "aic_clone_owner_";
        // Store in the submarine/campaign's native item data, not a global file or entity ID.
        [Serialize("", IsPropertySaveable.Yes)] public string CloneOwnerKey { get; set; }
        [Serialize("", IsPropertySaveable.Yes)] public string StoredClone { get; set; }
        [Serialize("", IsPropertySaveable.Yes)] public string CloneSubstrateId { get; set; }
        [Serialize("", IsPropertySaveable.Yes)] public string ClonePollution { get; set; }
        private double nextRestoreCheck;
        private bool restoreFailed;
        private bool transitionSnapshotCaptured;
        internal static readonly System.Collections.Generic.HashSet<AICCloneChamber> PendingRestores = new();

        private void MarkOriginal(Character original)
        {
            if (string.IsNullOrEmpty(CloneOwnerKey)
                || original.Info.GetSavedStatValue(StatTypes.None, CloneOwnerKey.ToIdentifier()) <= 0)
                CloneOwnerKey = OwnerMarkerPrefix + Guid.NewGuid().ToString("N");
            original.Info.ChangeSavedStatValue(StatTypes.None, 1, CloneOwnerKey.ToIdentifier(),
                removeOnDeath: false, maxValue: 1, setValue: true);
        }

        private static void RemoveOwnerMarkers(XElement data)
        {
            data.Element("savedstatvalues")?.Elements("savedstatvalue")
                .Where(e => ((string)e.Attribute("statidentifier") ?? "").StartsWith(OwnerMarkerPrefix, StringComparison.Ordinal))
                .Remove();
        }

        internal void CaptureClone()
        {
            // SavePlayers deletes inventories before the submarine is serialized.
            // Keep the snapshot taken before that cleanup, including intentionally empty inventories.
            if (transitionSnapshotCaptured) return;
            if (!Authority || Clone == null || Clone.Removed) return;
            if (Clone.IsDead) { StoredClone = ""; return; }
            if (Original?.Info == null) return;
            MarkOriginal(Original);
            var data = new XElement("clone", new XAttribute("version", 1),
                new XAttribute("basevitality", baseVitality));
            var info = Clone.Info.Save(data);
            RemoveOwnerMarkers(info);
            info.SetAttributeValue("hull", null);
            var inventory = new XElement("inventory");
            Character.SaveInventory(Clone.Inventory, inventory);
            data.Add(inventory);
            var health = new XElement("health");
            Clone.CharacterHealth.Save(health);
            data.Add(health);
            StoredClone = data.ToString(SaveOptions.DisableFormatting);
        }

        internal void CaptureCloneForTransition()
        {
            if (!Authority || Clone == null || Clone.Removed) return;
            CaptureClone();
            transitionSnapshotCaptured = true;
        }

        public override XElement Save(XElement parentElement)
        {
            CaptureClone();
            return base.Save(parentElement);
        }

        private void RestoreStoredClone()
        {
            // Item.Update runs inside the active item enumeration. Restoring equipment
            // there changes that collection; defer until MapEntity.UpdateAll has finished.
            if (Authority && (Clone == null || Clone.Removed) && !creating && !restoreFailed
                && !string.IsNullOrEmpty(StoredClone) && Timing.TotalTime >= nextRestoreCheck)
                PendingRestores.Add(this);
        }

        internal static void RestorePendingClones()
        {
            if (PendingRestores.Count == 0) return;
            var pending = PendingRestores.ToArray();
            PendingRestores.Clear();
            foreach (var chamber in pending)
                if (!chamber.Item.Removed && chamber.IsDeployed) chamber.RestoreStoredCloneNow();
        }

        private void RestoreStoredCloneNow()
        {
            if (Clone?.Removed == true)
            {
                Clones.Remove(Clone);
                if (Original != null) Bodies.Remove(Original);
                Clone = null;
                Original = null;
                Connected = false;
                transitionSnapshotCaptured = false;
                nextRestoreCheck = 0;
            }
            if (Clone != null || creating || restoreFailed || string.IsNullOrEmpty(StoredClone)
                || string.IsNullOrEmpty(CloneOwnerKey) || Item.Removed || Entity.Spawner == null
                || GameMain.GameSession?.IsRunning != true || GameMain.GameSession.RoundEnding
                || Timing.TotalTime < nextRestoreCheck) return;
            nextRestoreCheck = Timing.TotalTime + 1;

            // Multiplayer player characters are not necessarily in CrewManager's
            // bot/crew info collection. Match a live entity by its persisted marker.
            // Never borrow a nearby occupant or match by display name.
            var marker = CloneOwnerKey.ToIdentifier();
            var original = Character.CharacterList.FirstOrDefault(character =>
                Alive(character) && character.Info != null && !Clones.ContainsKey(character)
                && character.Info.GetSavedStatValue(StatTypes.None, marker) > 0);
            if (!Alive(original) || Bodies.ContainsKey(original)) return;

            Character spawned = null;
            creating = true;
            try
            {
                var data = XElement.Parse(StoredClone);
                if (data.Name != "clone" || (int?)data.Attribute("version") != 1
                    || data.Element("Character") == null || data.Element("inventory") == null || data.Element("health") == null)
                    throw new InvalidOperationException("Invalid clone snapshot.");
                float vitality = (float?)data.Attribute("basevitality") ?? 100;
                if (!float.IsFinite(vitality)) throw new InvalidOperationException("Invalid clone vitality.");
                baseVitality = MathHelper.Clamp(vitality, 1, 10000);
                var infoData = new XElement(data.Element("Character"));
                RemoveOwnerMarkers(infoData);
                infoData.SetAttributeValue("startitemsgiven", true);
                infoData.SetAttributeValue("hull", null);
                var info = new CharacterInfo(new ContentXElement(null, infoData));
                info.TeamID = original.TeamID;
                spawned = Character.Create(info, Item.WorldPosition + SpawnOffset,
                    Guid.NewGuid().ToString(), hasAi: true, spawnInitialItems: false);
                spawned.TeamID = original.TeamID;
                spawned.CharacterHealth.SetVitality(baseVitality);
                spawned.LoadTalents();
                spawned.SpawnInventoryItems(spawned.Inventory, new ContentXElement(null, data.Element("inventory")));
                spawned.CharacterHealth.Load(data.Element("health"));
                spawned.IsForceRagdolled = true;
                spawned.IsRagdolled = true;
                spawned.HealthUpdateInterval = 0;
                Original = original;
                Clone = spawned;
                Bodies[original] = this;
                Clones[spawned] = this;
                Connected = false;
                ApplyBodyState();
                Sync();
                // Keep the snapshot until the next save; an immediate save during loading
                // must not lose the clone. Clone != null prevents any duplicate restoration.
            }
            catch (Exception error)
            {
                // Preserve the serialized data for recovery, but do not retry every frame
                // and multiply partially loaded equipment after an invalid mod item fails.
                restoreFailed = true;
                if (spawned != null)
                {
                    Clones.Remove(spawned);
                    if (Clone == spawned)
                    {
                        Bodies.Remove(original);
                        Clone = null;
                        Original = null;
                    }
                    foreach (var item in spawned.Inventory.GetAllItems(true).Distinct().ToArray())
                        Entity.Spawner.AddItemToRemoveQueue(item);
                    Entity.Spawner.AddEntityToRemoveQueue(spawned);
                }
                LuaCsLogger.LogError("[AIC CloneChamber] Restore failed; snapshot retained: " + error);
            }
            finally { creating = false; }
        }
    }
}
