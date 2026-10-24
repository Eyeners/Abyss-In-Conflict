using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace AIC.Components
{
    // XML 中的 <ScalingWearable> 对应这个组件；保持 internal，与原版基类访问级别一致。
    class ScalingWearable : Wearable
    {
        private const float MIN_SCALE = 0.1f;
        private const float MAX_SCALE = 10.0f;

        [Serialize(1.0f, IsPropertySaveable.Yes, description: "Scale multiplier (0.1-10.0)")]
        public float ScaleMultiplier { get; set; }
        [Serialize(false, IsPropertySaveable.No)]
        public bool InheritLightScale { get; set; }
#if CLIENT
        private readonly Dictionary<LightComponent, (float sprite, Vector2 offset, float range)> lightBases = new();
        private static readonly System.Reflection.FieldInfo spritesField =
            HarmonyLib.AccessTools.Field(typeof(Wearable), "wearableSprites");
#endif
        private void ScaleLights(float factor)
        {
#if CLIENT
            if (!InheritLightScale || spritesField.GetValue(this) is not WearableSprite[] sprites) return;
            foreach (var sprite in sprites)
                foreach (var light in sprite.LightComponents)
                {
                    if (!lightBases.TryGetValue(light, out var original))
                    {
                        original = (light.LightSpriteScale, light.LightOffset, light.Range);
                        lightBases.Add(light, original);
                    }
                    // Apply the armour's relative multiplier once. Head Params.Scale=0.5 is
                    // already intentionally ignored by these hand-aligned wearable sprites.
                    light.LightSpriteScale = original.sprite * factor;
                    light.LightOffset = original.offset * factor;
                    light.Range = original.range * factor;
                    if (light.Light != null) light.Light.SpriteScale = Vector2.One * Item.Scale * light.LightSpriteScale;
                    light.SetLightSourceTransform();
                }
#endif
        }

        private HumanRagdollParams originalRagdoll;
        private bool hasModified = false;
        private Character scaledCharacter;

        // Item.Equip also runs for hand slots. A wearable callback alone is not proof of wearing.
        private bool IsActuallyWorn(Character character)
        {
            if (character?.Inventory is not CharacterInventory inventory) return false;
            const InvSlotType excluded = InvSlotType.Any | InvSlotType.LeftHand | InvSlotType.RightHand;
            bool hasWearSlot = false;
            foreach (var allowed in AllowedSlots)
            {
                var wearSlots = allowed & ~excluded;
                foreach (InvSlotType slot in System.Enum.GetValues(typeof(InvSlotType)))
                {
                    if (slot == InvSlotType.None || (slot & excluded) != 0 || !wearSlots.HasFlag(slot)) continue;
                    hasWearSlot = true;
                    if (!inventory.IsInLimbSlot(Item, slot)) return false;
                }
            }
            return hasWearSlot;
        }

        private void RebindSprites(Character character)
        {
            base.Equip(character);
            ScaleLights(hasModified ? ScaleMultiplier : 1);
        }

        private static void RebindWearables(Character character, IEnumerable<Item> items)
        {
            // Recreate invalidates wearable limb caches, but does not require replaying every
            // component's Equip callback (weapons, scaling components and other mod hooks).
            foreach (var item in items.Distinct())
            {
                if (item == null || item.Removed) continue;
                foreach (var wearable in item.GetComponents<Wearable>())
                {
                    if (wearable is ScalingWearable scaling)
                    {
                        if (scaling.IsActuallyWorn(character)) scaling.RebindSprites(character);
                    }
                    else wearable.Equip(character);
                }
            }
        }

        private static void Rebuild(Character character, HumanRagdollParams target, Item excluded = null)
        {
            var controller = character.AnimController;
            var position = character.WorldPosition;
            var velocity = controller.Collider.LinearVelocity;
            var equipped = new HashSet<Item>();
            for (int i = 0; i < character.Inventory.Capacity; i++)
            {
                var item = character.Inventory.GetItemAt(i);
                if (item != null && item != excluded && character.HasEquippedItem(item)) equipped.Add(item);
            }
            if (character.Info != null) character.Info.Ragdoll = target;
            // The shared collider lifecycle releases custom collider owners before
            // native Recreate replaces limbs, joints and collider indices.
            controller.Recreate(target);
            character.TeleportTo(position);
            controller.Collider.LinearVelocity = velocity;
            RebindWearables(character, equipped);
        }

        private static Dictionary<int, HumanRagdollParams> cloneCache =
            new Dictionary<int, HumanRagdollParams>();

        public ScalingWearable(Item item, ContentXElement element) : base(item, element)
        {
            ScaleMultiplier = MathHelper.Clamp(ScaleMultiplier, MIN_SCALE, MAX_SCALE);
        }

        public override void Equip(Character character)
        {
            if (!ValidateCharacter(character) || !IsActuallyWorn(character)) return;
            // 先挂上穿戴精灵。后面重建肢体会使旧肢体引用失效，需要再次绑定装备。
            base.Equip(character);

            if (hasModified) return;
            if (!ValidateCharacter(character)) return;

            originalRagdoll = character.AnimController.RagdollParams as HumanRagdollParams;
            if (originalRagdoll == null) return;

            // 重绑装备也会回调 Equip，必须先置位，防止递归重建。
            hasModified = true;
            scaledCharacter = character;

            var cloned = GetOrCreateClonedRagdoll(originalRagdoll, ScaleMultiplier);
            if (cloned == null)
            {
                hasModified = false;
                scaledCharacter = null;
                return;
            }

            Rebuild(character, cloned);

        }

        public override void Unequip(Character character)
        {
            // Detach from live limbs before rebuilding them, never from disposed old limbs.
            base.Unequip(character);
            ScaleLights(1);
            // 卸甲恢复原参数，并重绑其余装备；不能把正在卸下的甲再次装备。
            if (hasModified && scaledCharacter == character && originalRagdoll != null && ValidateCharacter(character))
            {
                hasModified = false;
                scaledCharacter = null;

                Rebuild(character, originalRagdoll, Item);

                originalRagdoll = null;
            }

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
            // 缓存的是参数模板，不是某个角色的物理实例；不要在外部按角色修改缓存内容。
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
            // 关节锚点也必须同比放大，否则碰撞体变大而连接位置仍停留在旧尺寸。
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
                // 深复制 XML，避免直接污染其他角色共用的原版参数。
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
