if CLIENT and Game.IsMultiplayer then return end

local EMP_IDENTIFIER = "emp"
local INFECTION_IDENTIFIER = "aic_nanite_infection"
local DIFFUSION_IDENTIFIER = "aic_nanite_diffusion"
local MINOR_MIRACLE_IDENTIFIER = "aic_minor_miracle"
local IFF_IDENTIFIER = "aic_nanite_iff_code"
local HOSTILE_BIOLOGY_IDENTIFIER = "aic_nanite_hostile_biology"
local HACKING_IDENTIFIER = "aic_hacking"
local HACKING_LOCK_IDENTIFIER = "aic_hacking_system_lock"
local IFF_UPDATE_INTERVAL = 1

local nextIffUpdateTime = 0

local function ClearNaniteStages(character)
    if character == nil or character.Removed or character.CharacterHealth == nil then
        return
    end

    character.CharacterHealth.ReduceAfflictionOnAllLimbs(INFECTION_IDENTIFIER, 1000, nil, nil)
    character.CharacterHealth.ReduceAfflictionOnAllLimbs(DIFFUSION_IDENTIFIER, 1000, nil, nil)
end

local function ClearEmpSensitiveAfflictions(character)
    if character == nil or character.CharacterHealth == nil then
        return
    end

    ClearNaniteStages(character)
    character.CharacterHealth.ReduceAfflictionOnAllLimbs(HACKING_IDENTIFIER, 1000, nil, nil)
    character.CharacterHealth.ReduceAfflictionOnAllLimbs(HACKING_LOCK_IDENTIFIER, 1000, nil, nil)
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
                ClearEmpSensitiveAfflictions(targetCharacter)
                return
            end
        end
    end
end

local function HasNaniteIff(character)
    if character == nil or character.Removed or character.CharacterHealth == nil then
        return false
    end

    return (character.CharacterHealth.GetAfflictionStrengthByIdentifier(IFF_IDENTIFIER, false) or 0) > 0
end

local function ConvertNaniteInfection(character)
    if character == nil or character.Removed or character.CharacterHealth == nil then return end
    local health = character.CharacterHealth
    if character.IsHusk or health.GetAfflictionStrengthByIdentifier(HOSTILE_BIOLOGY_IDENTIFIER, false) > 0 then
        local miracle = health.GetAffliction(MINOR_MIRACLE_IDENTIFIER)
        local prefab = AfflictionPrefab.Prefabs[INFECTION_IDENTIFIER]
        if miracle ~= nil and miracle.Strength > 0 and prefab ~= nil then
            -- A null target spreads a limb-specific affliction to all health regions natively.
            -- Undo max-vitality scaling because the measured miracle dose was already scaled.
            local remaining = math.min(prefab.MaxStrength, miracle.Strength) * character.MaxVitality / 100
            -- Keep each temporary dose within its prefab cap on high-vitality creatures.
            while remaining > 0 do
                local amount = math.min(prefab.MaxStrength, remaining)
                health.ApplyAffliction(nil, prefab.Instantiate(amount, nil), true, false, false)
                remaining = remaining - amount
            end
            miracle.Strength = 0
        end
        -- Hostile biology never converts infection back through an IFF signal.
        return
    end
    if not HasNaniteIff(character) or character.AnimController == nil then
        return
    end

    -- Most IFF wearers are not infected; avoid per-limb lookups on ordinary hits.
    if (health.GetAfflictionStrengthByIdentifier(INFECTION_IDENTIFIER, true) or 0) <= 0 then
        return
    end
    local miraclePrefab = AfflictionPrefab.Prefabs[MINOR_MIRACLE_IDENTIFIER]
    local infectionPrefab = AfflictionPrefab.Prefabs[INFECTION_IDENTIFIER]
    if miraclePrefab == nil or infectionPrefab == nil then
        return
    end

    local convertedStrength = 0
    for limb in character.AnimController.Limbs do
        local healthIndex = limb.HealthIndex
        if healthIndex ~= nil and healthIndex >= 0 then
            local infection = health.GetAffliction(infectionPrefab.Identifier, limb)
            if infection ~= nil and infection.Strength > 0 then
                convertedStrength = convertedStrength + infection.Strength
                -- Clear immediately; another limb referencing this infection now sees zero.
                infection.Strength = 0
            end
        end
    end
    if convertedStrength > 0 then
        local miracle = health.GetAffliction(miraclePrefab.Identifier)
        local previousStrength = miracle ~= nil and miracle.Strength or 0
        if miracle == nil then
            health.ApplyAffliction(nil, miraclePrefab.Instantiate(convertedStrength, nil), true, false, false)
            miracle = health.GetAffliction(miraclePrefab.Identifier)
        end
        -- Transfer measured doses exactly, without applying max-vitality scaling a second time.
        if miracle ~= nil then
            miracle.Strength = math.min(miraclePrefab.MaxStrength, previousStrength + convertedStrength)
        end
    end
end

local function IsMiracle(affliction)
    return affliction ~= nil and tostring(affliction.Prefab.Identifier.Value) == MINOR_MIRACLE_IDENTIFIER
end

-- Convert after insertion, before the next health update can consume a tiny spray dose.
-- Infection insertions do not match IsMiracle, so conversion cannot recurse into itself.
Hook.Patch("aic_nanite_miracle_after_affliction", "Barotrauma.CharacterHealth", "ApplyAffliction", function(health, parameters)
    if IsMiracle(parameters["affliction"]) then
        ConvertNaniteInfection(health.Character)
    end
end, Hook.HookMethodType.After)

Hook.Patch("aic_nanite_miracle_after_damage", "Barotrauma.CharacterHealth", "ApplyDamage", function(health, parameters)
    local result = parameters["attackResult"]
    if result == nil or result.Afflictions == nil then return end
    for affliction in result.Afflictions do
        if IsMiracle(affliction) then
            ConvertNaniteInfection(health.Character)
            return
        end
    end
end, Hook.HookMethodType.After)

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

Hook.Add("roundEnd", "AIC_NaniteIffReset", function()
    nextIffUpdateTime = 0
end)
