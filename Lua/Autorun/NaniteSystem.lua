if CLIENT and Game.IsMultiplayer then return end

local EMP_IDENTIFIER = "emp"
local INFECTION_IDENTIFIER = "aic_nanite_infection"
local DIFFUSION_IDENTIFIER = "aic_nanite_diffusion"

local function ClearNaniteStages(character)
    if character == nil or character.CharacterHealth == nil then
        return
    end

    character.CharacterHealth.ReduceAfflictionOnAllLimbs(INFECTION_IDENTIFIER, 1000, nil, nil)
    character.CharacterHealth.ReduceAfflictionOnAllLimbs(DIFFUSION_IDENTIFIER, 1000, nil, nil)
end

local function ProcessNaniteDamageEvent(targetCharacter, afflictions)
    if targetCharacter == nil or afflictions == nil then
        return
    end

    for affliction in afflictions do
        local prefab = affliction.Prefab
        if prefab ~= nil and prefab.Identifier ~= nil then
            local identifier = string.lower(tostring(prefab.Identifier.Value))

            if identifier == EMP_IDENTIFIER then
                ClearNaniteStages(targetCharacter)
                return
            end
        end
    end
end

Hook.Add("character.damageLimb", "AIC_NaniteDamageLimb", function(character, worldPosition, hitLimb, afflictions, stun, playSound, attackImpulse, attacker, damageMultiplier, allowStacking, penetration, shouldImplode)
    ProcessNaniteDamageEvent(character, afflictions)
end)
