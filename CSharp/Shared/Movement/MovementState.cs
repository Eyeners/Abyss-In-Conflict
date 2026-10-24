using Barotrauma;
using Barotrauma.Items.Components;

namespace AIC.Components
{
    // Compose these predicates: a vehicle may retain an incapacitated rider while
    // an action such as jumping or crawling requires an active wearer.
    internal static class MovementState
    {
        internal static bool Humanoid(Character c) => c != null && !c.Removed
            && c.AnimController is HumanoidAnimController;
        internal static bool Conscious(Character c) => c != null && !c.Removed && !c.IsDead
            && !c.IsIncapacitated && !c.IsRagdolled && !c.IsForceRagdolled && c.Stun <= 0;
        internal static bool OnLand(Character c) => c != null && !c.InWater && !c.IsClimbing
            && !c.IsAttachedToController();
        internal static bool SelectingLadder(Character c) => c?.SelectedItem?.GetComponent<Ladder>() != null
            || c?.SelectedSecondaryItem?.GetComponent<Ladder>() != null;
        internal static bool ActiveOnLand(Character c) => Humanoid(c) && Conscious(c) && OnLand(c);
    }
}
