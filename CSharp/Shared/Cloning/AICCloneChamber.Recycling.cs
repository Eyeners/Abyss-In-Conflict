using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;

namespace AIC.Components
{
    partial class AICCloneChamber
    {
        private bool recycling;
        private sealed class FrameRecord { internal string Substrate, Pollution; }
        private static readonly ConditionalWeakTable<Character, FrameRecord> RecoveredFrames = new();
        private void RememberFrame(Character character)
        {
            if (character == null) return;
            RecoveredFrames.Remove(character);
            RecoveredFrames.Add(character, new FrameRecord { Substrate = CloneSubstrateId, Pollution = ClonePollution });
        }
        private ushort recycleOccupantId;
        private Character RecycleTarget => Seat?.User is Character occupant
            && (IsRecoverable(occupant) || (!Authority && occupant.ID == recycleOccupantId)) ? occupant : null;

        private static bool IsRecoverable(Character target)
        {
            if (target == null || target.Removed || target.IsPlayer || Bodies.ContainsKey(target)) return false;
            if (Clones.TryGetValue(target, out var chamber)) return !chamber.Connected;
            if (RecoveredFrames.TryGetValue(target, out _)) return true;
            return target.SpeciesName == "aic_exsequent_frame_hlf01";
        }

        internal void TryInsertFrame(Character user)
        {
            if (!Authority || !IsDeployed || Item.Condition <= 0 || Connected || recycling
                || user == null || user.IsIncapacitated || !user.CanInteractWith(Item)
                || IsMaintenanceInteraction(user) || Seat == null || Seat.User != null) return;
            var target = user.SelectedCharacter;
            if (!IsRecoverable(target) || target.TeamID != user.TeamID) return;
            user.DeselectCharacter();
            Seat.User = target;
            Seat.IsActive = true;
            target.SelectedItem = Item;
            user.SelectedItem = Item;
            Sync();
#if SERVER
            Item.CreateServerEvent(Seat);
#endif
        }

        private bool CanRecycle(Character user) => !recycling && IsDeployed && Item.Condition > 0
            && !Connected && RecycleTarget != null && user != null && !user.Removed && !user.IsDead
            && !user.IsIncapacitated && user.TeamID == RecycleTarget.TeamID
            && user.IsAnySelectedItem(Item) && user.CanInteractWith(Item)
            && Item.GetComponent<ItemContainer>()?.Inventory.GetItemAt(0) == null;

        private void Recycle(Character user)
        {
            if (!Authority || !CanRecycle(user) || Entity.Spawner == null) return;
            var target = RecycleTarget;
            Clones.TryGetValue(target, out var source);
            RecoveredFrames.TryGetValue(target, out var record);
            string id = source?.CloneSubstrateId ?? record?.Substrate;
            string pollution = source?.ClonePollution ?? record?.Pollution;
            if (string.IsNullOrEmpty(id))
                id = target.SpeciesName == "aic_exsequent_frame_hlf01" ? "aic_exsequent_frame_hlf01_substrate"
                    : source?.baseVitality > 100 ? "aic_clone_substrate_enhanced" : "aic_clone_substrate_standard";
            var prefab = ItemPrefab.Find(null, id.ToIdentifier());
            if (prefab == null) return;
            recycling = true;
            Entity.Spawner.AddItemToSpawnQueue(prefab, Item.WorldPosition, onSpawned: material =>
            {
                recycling = false;
                if (material == null || material.Removed) return;
                var inventory = Item.GetComponent<ItemContainer>()?.Inventory;
                if (Item.Removed || !CanRecycle(user) || RecycleTarget != target || inventory == null
                    || !inventory.TryPutItem(material, 0, allowSwapping: false, allowCombine: false, user: user))
                {
                    Entity.Spawner.AddItemToRemoveQueue(material);
                    return;
                }
                // Return equipment intact; removing the frame must not delete its inventory.
                material.GetComponent<AICCloneSubstrate>()?.Recover(pollution, target.IsDead);
                if (target.Inventory != null)
                    foreach (var equipment in target.Inventory.AllItems.Distinct().ToArray()) equipment.Drop(target);
                Seat.Select(target);
                if (source != null)
                {
                    source.StoredClone = "";
                    source.CloneSubstrateId = "";
                    source.ClonePollution = "";
                    source.CloneOwnerKey = "";
                    Clones.Remove(target);
                    if (source.Original != null) Bodies.Remove(source.Original);
                    source.Clone = null;
                    source.Original = null;
                    source.Sync();
                }
                Entity.Spawner.AddEntityToRemoveQueue(target);
                RecoveredFrames.Remove(target);
            });
        }
    }
}
