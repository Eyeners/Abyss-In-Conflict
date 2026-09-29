local SUPPRESSION_THRESHOLD = 20
local HEAVY_LOAD_THRESHOLD = 0.1

local function ShouldProne(character)
    if not character or character.Removed or not character.IsHumanoid or character.IsDead
        or character.IsIncapacitated or character.IsClimbing or character.InWater
        or not character.CharacterHealth then return false end
    local health = character.CharacterHealth
    return health.GetAfflictionStrengthByIdentifier("aic_suppression") >= SUPPRESSION_THRESHOLD
        or health.GetAfflictionStrengthByIdentifier("aic_heavy_load") > HEAVY_LOAD_THRESHOLD
end

-- Match Neurotrauma's prone collider fix; XML supplies temporary ground animations.
-- Humanoid is the animation/rig class, not the human species (husks also use it).
-- Do not write persistent collider dimensions or force ragdoll/stun/limb damage.
Hook.Patch("AIC.CombatProne.Control", "Barotrauma.Character", "Control", function(character)
    if ShouldProne(character) then
        character.SetInput(InputType.Crouch, false, true)
    end
end, Hook.HookMethodType.Before)

Hook.Patch("AIC.CombatProne.Collider", "Barotrauma.Ragdoll", "get_ColliderHeightFromFloor",
    function(ragdoll)
        if ShouldProne(ragdoll.Character) then return Single(0.1) end
    end, Hook.HookMethodType.After)
