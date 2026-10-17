using Barotrauma;

namespace AIC.Components
{
    partial class AICCloneChamber
    {
        private static readonly Identifier DamageWarningId = "aic_clone_chamber_warning".ToIdentifier();
        private float previousCondition = float.NaN;
        private bool warningMayBeActive;

        private void UpdateDamageWarning()
        {
            bool damaged = Item.Condition < previousCondition;
            previousCondition = Item.Condition;
#if SERVER
            bool controlling = owner != null && owner.Character == Clone;
#else
            bool controlling = Character.Controlled == Clone;
#endif
            if (!Connected || !Alive(Original) || !Alive(Clone) || Seat?.User != Original || !controlling)
            {
                ClearDamageWarning();
                return;
            }
            if (!damaged) return;
            var health = Clone.CharacterHealth;
            var warning = health.GetAffliction(DamageWarningId);
            if (warning == null && AfflictionPrefab.Prefabs.TryGet(DamageWarningId, out var prefab))
            {
                health.ApplyAffliction(null, prefab.Instantiate(2));
                warning = health.GetAffliction(DamageWarningId);
            }
            if (warning != null)
            {
                warningMayBeActive = true;
                // Visual warning only; refresh rather than stack on rapid hits.
                warning.Strength = 2;
                Clone.HealthUpdateInterval = 0;
            }
        }

        private void ClearDamageWarning()
        {
            if (!warningMayBeActive) return;
            warningMayBeActive = false;
            var warning = Clone?.CharacterHealth?.GetAffliction(DamageWarningId);
            if (warning == null || warning.Strength <= 0) return;
            warning.Strength = 0;
            Clone.HealthUpdateInterval = 0;
        }
    }
}
