if CLIENT and Game.IsMultiplayer then return end

local RADIATION_IDENTIFIER = "radiationsickness"
local MACHINE_RADIATION_IDENTIFIER = "aic_machine_radiation"
local UPDATE_INTERVAL = 0.25

local nextUpdateTime = 0

local function IsMachineCharacter(character)
    if character == nil then
        return false
    end

    if character.IsMachine ~= nil then
        return character.IsMachine
    end

    if character.Params ~= nil and character.Params.IsMachine ~= nil then
        return character.Params.IsMachine
    end

    return false
end

local function GetAfflictionStrength(character, identifier)
    if character == nil or character.CharacterHealth == nil then
        return 0
    end

    return character.CharacterHealth.GetAfflictionStrengthByIdentifier(identifier, true) or 0
end

local function ApplyAffliction(character, identifier, strength)
    if character == nil or character.CharacterHealth == nil or strength <= 0 then
        return
    end

    local prefab = AfflictionPrefab.Prefabs[identifier]
    if prefab == nil then
        return
    end

    local affliction = prefab.Instantiate(strength, nil)
    local limb = nil
    if character.AnimController ~= nil then
        limb = character.AnimController.MainLimb
    end

    character.CharacterHealth.ApplyAffliction(limb, affliction, true, false, false)
end

local function ConvertMachineRadiation(character)
    local radiationStrength = GetAfflictionStrength(character, RADIATION_IDENTIFIER)
    if radiationStrength <= 0 then
        return
    end

    local convertedStrength = radiationStrength
    ApplyAffliction(character, MACHINE_RADIATION_IDENTIFIER, convertedStrength)
    character.CharacterHealth.ReduceAfflictionOnAllLimbs(RADIATION_IDENTIFIER, convertedStrength + 1, nil, nil)
end

Hook.Add("think", "ST_MachineRadiationConversion", function()
    local now = Timer.GetTime()
    if now < nextUpdateTime then
        return
    end

    nextUpdateTime = now + UPDATE_INTERVAL

    for character in Character.CharacterList do
        if IsMachineCharacter(character) then
            ConvertMachineRadiation(character)
        end
    end
end)
