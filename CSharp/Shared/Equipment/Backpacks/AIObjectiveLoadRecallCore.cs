using Barotrauma;
using Barotrauma.Items.Components;

namespace AIC.Components
{
    internal sealed class AIObjectiveLoadRecallCore : AIObjectiveContainItem
    {
        public override bool AllowInAnySub => true;
        public override bool AllowOutsideSubmarine => true;

        internal AIObjectiveLoadRecallCore(Character character, ItemContainer container, AIObjectiveManager manager)
            : base(character, "aic_recall_core".ToIdentifier(), container, manager) { }

        public override void Act(float deltaTime)
        {
            base.Act(deltaTime);
            // ContainItem checks percentage, but GetItem checks absolute condition.
            // A core has only 9 condition: forwarding 33.33 would reject even full cores.
            foreach (var objective in SubObjectives)
                if (objective is AIObjectiveGetItem getItem) getItem.TargetCondition = 3;
        }
    }
}
