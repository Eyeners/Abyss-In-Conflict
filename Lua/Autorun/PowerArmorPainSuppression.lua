local function HasAffliction(character, identifier, minStrength)
    if character == nil or character.CharacterHealth == nil then
        return false
    end

    local strength = character.CharacterHealth.GetAfflictionStrengthByIdentifier(identifier, true) or 0
    if strength <= 0 then
        return false
    end

    return strength >= (minStrength or 0.5)
end

Hook.Patch("Barotrauma.Character", "CalculateMovementPenalty", function(instance, ptable)
    if HasAffliction(instance, "aic_wardens_assisted_exoskeleton", 0.5) then
        ptable.PreventExecution = true
        return 0
    end
end, Hook.HookMethodType.Before)
