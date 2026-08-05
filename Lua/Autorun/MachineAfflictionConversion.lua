if CLIENT and Game.IsMultiplayer then return end

local CONVERSIONS = {
    explosiondamage = {
        identifier = "aic_machine_structural_damage",
        multiplier = 0.5
    },
    internaldamage = {
        identifier = "aic_machine_component_damage",
        multiplier = 0.5
    }
}

local function IsMachineCharacter(character)
    if character == nil then
        return false
    end

    if character.IsMachine ~= nil then
        return character.IsMachine
    end

    return character.Params ~= nil and character.Params.IsMachine == true
end

local function GetIdentifier(affliction)
    if affliction == nil or affliction.Prefab == nil then
        return nil
    end

    return string.lower(tostring(affliction.Prefab.Identifier.Value))
end

local function CreateConvertedAffliction(affliction)
    local conversion = CONVERSIONS[GetIdentifier(affliction)]
    if conversion == nil then
        return nil
    end

    local prefab = AfflictionPrefab.Prefabs[conversion.identifier]
    if prefab == nil then
        return nil
    end

    return prefab.Instantiate(affliction.Strength * conversion.multiplier, nil)
end

-- Status effects and Lua commonly apply afflictions through this method.
Hook.Patch("aic_machine_convert_apply_affliction", "Barotrauma.CharacterHealth", "ApplyAffliction", function(instance, ptable)
    if not IsMachineCharacter(instance.Character) then
        return
    end

    local converted = CreateConvertedAffliction(ptable["affliction"])
    if converted ~= nil then
        ptable["affliction"] = converted
    end
end, Hook.HookMethodType.Before)

-- Weapon attacks bypass ApplyAffliction and feed an AttackResult directly into ApplyDamage.
Hook.Patch("aic_machine_convert_apply_damage", "Barotrauma.CharacterHealth", "ApplyDamage", function(instance, ptable)
    if not IsMachineCharacter(instance.Character) then
        return
    end

    local hitLimb = ptable["hitLimb"]
    local attackResult = ptable["attackResult"]
    if hitLimb == nil or attackResult == nil or attackResult.Afflictions == nil then
        return
    end

    for affliction in attackResult.Afflictions do
        local converted = CreateConvertedAffliction(affliction)
        if converted ~= nil then
            instance.ApplyAffliction(hitLimb, converted, true, false, false)
            affliction.Strength = 0
        end
    end
end, Hook.HookMethodType.Before)
