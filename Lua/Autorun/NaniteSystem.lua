if CLIENT and Game.IsMultiplayer then return end

local EMP_IDENTIFIER = "emp"
local INFECTION_IDENTIFIER = "aic_nanite_infection"
local DIFFUSION_IDENTIFIER = "aic_nanite_diffusion"
local MINOR_MIRACLE_IDENTIFIER = "aic_minor_miracle"
local IFF_IDENTIFIER = "aic_nanite_iff_code"
local IFF_UPDATE_INTERVAL = 1

local nextIffUpdateTime = 0

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

local function HasNaniteIff(character)
    if character == nil or character.CharacterHealth == nil then
        return false
    end

    return (character.CharacterHealth.GetAfflictionStrengthByIdentifier(IFF_IDENTIFIER, false) or 0) > 0
end

local function ConvertNaniteInfection(character)
    if not HasNaniteIff(character) or character.AnimController == nil then
        return
    end

    local health = character.CharacterHealth
    local miraclePrefab = AfflictionPrefab.Prefabs[MINOR_MIRACLE_IDENTIFIER]
    local infectionPrefab = AfflictionPrefab.Prefabs[INFECTION_IDENTIFIER]
    if miraclePrefab == nil or infectionPrefab == nil then
        return
    end

    -- Multiple visual limbs can share one health index, so process each limb health only once.
    local processedHealthIndices = {}
    for limb in character.AnimController.Limbs do
        local healthIndex = limb.HealthIndex
        if healthIndex ~= nil and healthIndex >= 0 and not processedHealthIndices[healthIndex] then
            processedHealthIndices[healthIndex] = true

            local infection = health.GetAffliction(infectionPrefab.Identifier, limb)
            if infection ~= nil and infection.Strength > 0 then
                local convertedStrength = infection.Strength
                health.ApplyAffliction(limb, miraclePrefab.Instantiate(convertedStrength, nil), true, false, false)
                health.ReduceAfflictionOnLimb(limb, INFECTION_IDENTIFIER, convertedStrength + 0.001, nil, nil)
            end
        end
    end
end

Hook.Add("character.damageLimb", "AIC_NaniteDamageLimb", function(character, worldPosition, hitLimb, afflictions, stun, playSound, attackImpulse, attacker, damageMultiplier, allowStacking, penetration, shouldImplode)
    ProcessNaniteDamageEvent(character, afflictions)
    ConvertNaniteInfection(character)
end)

Hook.Add("think", "AIC_NaniteIffConversion", function()
    local now = Timer.GetTime()
    if now < nextIffUpdateTime then
        return
    end

    nextIffUpdateTime = now + IFF_UPDATE_INTERVAL

    for character in Character.CharacterList do
        ConvertNaniteInfection(character)
    end
end)
