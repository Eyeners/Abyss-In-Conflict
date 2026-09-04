using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace AIC.Components
{
    class ScalingWearable : Wearable
    {
        private const float MIN_SCALE = 0.1f;
        private const float MAX_SCALE = 10.0f;

        [Serialize(1.0f, IsPropertySaveable.Yes, description: "Scale multiplier (0.1-10.0)")]
        public float ScaleMultiplier { get; set; }

        private HumanRagdollParams originalRagdoll;
        private bool hasModified = false;

        private static Dictionary<int, HumanRagdollParams> cloneCache =
            new Dictionary<int, HumanRagdollParams>();

        public ScalingWearable(Item item, ContentXElement element) : base(item, element)
        {
            ScaleMultiplier = MathHelper.Clamp(ScaleMultiplier, MIN_SCALE, MAX_SCALE);
        }

        public override void Equip(Character character)
        {
            base.Equip(character);

            if (hasModified) return;
            if (!ValidateCharacter(character)) return;

            originalRagdoll = character.AnimController.RagdollParams as HumanRagdollParams;
            if (originalRagdoll == null) return;

            hasModified = true;

            var position = character.WorldPosition;
            var velocity = character.AnimController.Collider.LinearVelocity;

            var equippedItems = new List<Item>();
            for (int i = 0; i < character.Inventory.Capacity; i++)
            {
                var equippedItem = character.Inventory.GetItemAt(i);
                if (equippedItem != null && character.HasEquippedItem(equippedItem))
                {
                    equippedItems.Add(equippedItem);
                }
            }

            var cloned = GetOrCreateClonedRagdoll(originalRagdoll, ScaleMultiplier);
            if (cloned == null)
            {
                hasModified = false;
                return;
            }

            if (character.Info != null)
            {
                character.Info.Ragdoll = cloned;
            }
            character.AnimController.Recreate(cloned);

            character.TeleportTo(position);
            character.AnimController.Collider.LinearVelocity = velocity;

            foreach (var equippedItem in equippedItems)
            {
                if (equippedItem != null && !equippedItem.Removed)
                {
                    equippedItem.Equip(character);
                }
            }

        }

        public override void Unequip(Character character)
        {
            if (hasModified && originalRagdoll != null && ValidateCharacter(character))
            {
                hasModified = false;

                var position = character.WorldPosition;
                var velocity = character.AnimController.Collider.LinearVelocity;

                var equippedItems = new List<Item>();
                for (int i = 0; i < character.Inventory.Capacity; i++)
                {
                    var equippedItem = character.Inventory.GetItemAt(i);
                    if (equippedItem != null
                        && character.HasEquippedItem(equippedItem)
                        && equippedItem != this.item)
                    {
                        equippedItems.Add(equippedItem);
                    }
                }

                if (character.Info != null)
                {
                    character.Info.Ragdoll = originalRagdoll;
                }
                character.AnimController.Recreate(originalRagdoll);

                character.TeleportTo(position);
                character.AnimController.Collider.LinearVelocity = velocity;

                foreach (var equippedItem in equippedItems)
                {
                    if (equippedItem != null && !equippedItem.Removed)
                    {
                        equippedItem.Equip(character);
                    }
                }

                originalRagdoll = null;
            }

            base.Unequip(character);
        }

        private bool ValidateCharacter(Character character)
        {
            if (character == null || character.Removed) return false;
            if (!character.IsHumanoid) return false;
            if (character.AnimController == null) return false;
            if (!(character.AnimController.RagdollParams is HumanRagdollParams)) return false;
            return true;
        }

        private HumanRagdollParams GetOrCreateClonedRagdoll(HumanRagdollParams original, float scale)
        {
            int cacheKey = GetCacheKey(original, scale);

            HumanRagdollParams cached;
            if (cloneCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            var cloned = CloneRagdollParams(original);
            if (cloned == null) return null;

            cloned.LimbScale = MathHelper.Clamp(
                original.LimbScale * scale,
                MIN_SCALE,
                MAX_SCALE
            );
            cloned.JointScale = MathHelper.Clamp(
                original.JointScale * scale,
                MIN_SCALE,
                MAX_SCALE
            );

            cloneCache[cacheKey] = cloned;

            if (cloneCache.Count > 20)
            {
                var oldestKey = cloneCache.Keys.First();
                cloneCache.Remove(oldestKey);
            }

            return cloned;
        }

        private int GetCacheKey(HumanRagdollParams ragdoll, float scale)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + ragdoll.GetHashCode();
                hash = hash * 31 + scale.GetHashCode();
                return hash;
            }
        }

        private HumanRagdollParams CloneRagdollParams(HumanRagdollParams original)
        {
            try
            {
                var clonedDoc = new XDocument(original.doc);
                var cloned = new HumanRagdollParams
                {
                    doc = clonedDoc,
                    IsLoaded = true
                };

                if (original.Path.Value != null)
                {
                    cloned.Path = original.Path;
                }

                cloned.CreateColliders();
                cloned.CreateLimbs();
                cloned.CreateJoints();
                cloned.Deserialize();

                return cloned;
            }
            catch (System.Exception exception)
            {
                LuaCsLogger.LogError("[ScalingWearable] Clone failed: " + exception.Message);
                return null;
            }
        }
    }
}
