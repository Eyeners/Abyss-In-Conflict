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
            // 先挂上穿戴精灵。后面重建肢体会使旧肢体引用失效，需要再次绑定装备。
            base.Equip(character);

            if (hasModified) return;
            if (!ValidateCharacter(character)) return;

            originalRagdoll = character.AnimController.RagdollParams as HumanRagdollParams;
            if (originalRagdoll == null) return;

            // 重绑装备也会回调 Equip，必须先置位，防止递归重建。
            hasModified = true;

            var position = character.WorldPosition;
            var velocity = character.AnimController.Collider.LinearVelocity;

            // 当前实现按槽收集，双手装备可能重复出现；这是待优化点，不在注释整理时改行为。
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
            // 真正重建碰撞体和关节，不是只放大贴图；客户端也需要相同的布娃娃参数。
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
            // 卸甲恢复原参数，并重绑其余装备；不能把正在卸下的甲再次装备。
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
