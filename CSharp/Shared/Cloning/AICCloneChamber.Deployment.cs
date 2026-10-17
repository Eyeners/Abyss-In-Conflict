using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using System.Linq;

namespace AIC.Components
{
    partial class AICCloneChamber
    {
        private Body deploymentBody;
        private World deploymentWorld;
        private float displayedCondition = float.NaN;
        private float displayedMaxCondition = float.NaN;
        private bool IsDeployed => Item.GetComponent<Holdable>()?.Attached == true;

        internal bool IsMaintenanceInteraction(Character user) => user != null
            && (Item.GetComponent<Repairable>()?.CurrentFixer == user
                || user.HeldItems.Any(tool => tool.HasIdentifierOrTags(new[] { "wrench".ToIdentifier(), "crowbar".ToIdentifier() })));

        private void UpdateDeployment(float deltaTime)
        {
            // Repair UI reads absolute condition; keep derived condition and the cached
            // sprite in step after repairs and saved-item restoration as well.
            if (Item.Condition != displayedCondition || Item.MaxCondition != displayedMaxCondition)
            {
                Item.RecalculateConditionValues();
                displayedCondition = Item.Condition;
                displayedMaxCondition = Item.MaxCondition;
#if CLIENT
                Item.SetActiveSprite();
#endif
            }
            var holdable = Item.GetComponent<Holdable>();
            var carrier = Item.ParentInventory?.Owner as Character;
            if (holdable != null && !holdable.Attached && carrier != null
                && !carrier.IsIncapacitated && carrier.HasEquippedItem(Item, predicate: CharacterInventory.IsHandSlotType)
                && carrier.IsKeyHit(InputType.Use))
            {
                // Reuse native attachment placement and client/server events for E deployment.
                bool held = carrier.IsKeyDown(InputType.Aim), hit = carrier.IsKeyHit(InputType.Aim);
                carrier.SetInput(InputType.Aim, hit, true);
                try { holdable.Use(deltaTime, carrier); }
                finally { carrier.SetInput(InputType.Aim, hit, held); }
            }
            if (!IsDeployed || Item.Removed)
            {
                ReleaseDeploymentBody();
                if (Authority)
                {
                    if (Connected) Disconnect();
                    if (Seat?.User is Character occupant) Seat.Select(occupant);
                }
                return;
            }
            if (GameMain.World == null) return;
            if (deploymentWorld != GameMain.World) ReleaseDeploymentBody();
            if (deploymentBody == null)
            {
                deploymentWorld = GameMain.World;
                deploymentBody = deploymentWorld.CreateBody(Vector2.Zero, 0, BodyType.Static);
                deploymentBody.UserData = Item;
                deploymentBody.CreateRectangle(ConvertUnits.ToSimUnits(140 * Item.Scale),
                    ConvertUnits.ToSimUnits(390 * Item.Scale), 1, Vector2.Zero,
                    Physics.CollisionItemBlocking, Physics.CollisionProjectile);
            }
            if (deploymentBody.Position != Item.SimPosition || deploymentBody.Rotation != 0)
                deploymentBody.SetTransform(Item.SimPosition, 0);
        }

        private void ReleaseDeploymentBody()
        {
            if (deploymentBody != null)
            {
                deploymentBody.Enabled = false;
                if (deploymentWorld != null && deploymentWorld.BodyList.Contains(deploymentBody))
                    deploymentWorld.Remove(deploymentBody);
            }
            deploymentBody = null;
            deploymentWorld = null;
        }
    }
}
