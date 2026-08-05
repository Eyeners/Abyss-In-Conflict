if not SERVER and Game.IsMultiplayer then
    return
end

AIC = AIC or {}
AIC.VCEProbabilityFields = AIC.VCEProbabilityFields or {
    Definitions = {},
    RegisteredConfigs = {},
    Initialized = false
}

local FieldSystem = AIC.VCEProbabilityFields
local INITIALIZATION_RETRY_MS = 100
local MAX_INITIALIZATION_RETRIES = 100
local InstallDamageLimbPatch

local function BuildVCEConfig(definition)
    return {
        name = definition.name or definition.identifier,
        type = "custom",
        ricochetchance = definition.ricochetchance or 0,
        aftereffectmultiplier = 1,
        correctionaffliction = nil,
        correctionmultiplier = 0,
        enablecorrection = false,
        penresistance = 0,
        maxhits = 0,
        maxcondition = 0,
        ignoredamage = true,
        isPlateCarrier = false,
        isHelmet = false,
        protectionarea = definition.protectionarea,
        targetidentifier = definition.targetidentifier,
        PlaySound = false,
        aicProbabilityFieldIdentifier = definition.identifier,
        override = true
    }
end

local function AddDefinitionToVCE(definition)
    if FieldSystem.RegisteredConfigs[definition.identifier] then
        return
    end

    VCE.ArmorSystem.AddtoMain({
        [definition.identifier] = BuildVCEConfig(definition)
    })
    FieldSystem.RegisteredConfigs[definition.identifier] = true
end

function FieldSystem.Register(definition)
    if definition == nil or definition.identifier == nil then
        return
    end

    definition.identifier = string.lower(definition.identifier)
    FieldSystem.Definitions[definition.identifier] = definition

    if FieldSystem.Initialized then
        AddDefinitionToVCE(definition)
    end
end

local function ResolveRicochetChance(data, definition, item, character, limb, affliction)
    local chance = data.ricochetchance or definition.ricochetchance or 0
    if type(chance) == "function" then
        chance = chance(item, character, limb, affliction) or 0
    end

    return VCE.HF.clamp(chance, 0, 1)
end

local function Initialize(retriesRemaining)
    if FieldSystem.Initialized then
        return
    end

    if VCE == nil or VCE.ArmorSystem == nil or VCE.ArmorSystem.PlateMain == nil or VCE.ArmorSystem.AddtoMain == nil or VCE.HF == nil then
        if retriesRemaining > 0 then
            Timer.Wait(function()
                Initialize(retriesRemaining - 1)
            end, INITIALIZATION_RETRY_MS)
        else
            print("[Abyss In Conflict] VCE was not loaded; probability fields are disabled.")
        end
        return
    end

    local originalPlateMain = VCE.ArmorSystem.PlateMain
    FieldSystem.OriginalPlateMain = originalPlateMain

    VCE.ArmorSystem.PlateMain = function(data, item, penetrationLevel, damageMultiplier, affliction, character, limb, rawPenetration)
        local fieldIdentifier = data and data.aicProbabilityFieldIdentifier
        local definition = fieldIdentifier and FieldSystem.Definitions[fieldIdentifier]
        if definition == nil then
            return originalPlateMain(data, item, penetrationLevel, damageMultiplier, affliction, character, limb)
        end

        if definition.canBlock ~= nil and not definition.canBlock(item, character, limb, affliction, penetrationLevel, rawPenetration) then
            return damageMultiplier, penetrationLevel, true
        end

        local chance = ResolveRicochetChance(data, definition, item, character, limb, affliction)
        if not VCE.HF.DoChance(chance) then
            return damageMultiplier, penetrationLevel, true
        end

        if definition.onBlock ~= nil then
            definition.onBlock(item, character, limb, affliction, penetrationLevel, rawPenetration)
        end

        return 0, 0, false
    end

    FieldSystem.Initialized = true
    for _, definition in pairs(FieldSystem.Definitions) do
        AddDefinitionToVCE(definition)
    end
    InstallDamageLimbPatch()
end

local BASTION_IDENTIFIER = "aic_wardens_bastion_experimental_powerarmor"
local BASTION_SHIELD_ENABLED_TAG = "aic_bastion_shield_on"
local BASTION_BATTERY_SLOT = 2
local BASTION_BURN_DRAIN_MULTIPLIER = 2
local ARCHON_IDENTIFIER = "aic_practitioner_archon_heavy_armor"
local ARCHON_SHIELD_ENABLED_TAG = "aic_archon_shield_on"
local ARCHON_BATTERY_SLOT = 2
local ARCHON_MIN_DAMAGE = 30
local ARCHON_MIN_RAW_PENETRATION = 0.3
local ARCHON_BATTERY_DRAIN = 25

local function BuildBastionTargetIdentifiers()
    local targetIdentifiers = {}

    for prefab in AfflictionPrefab.Prefabs do
        local afflictionType = string.lower(tostring(prefab.AfflictionType.Value))
        local identifier = string.lower(tostring(prefab.Identifier.Value))
        if (afflictionType == "damage" and identifier ~= "organdamage") or afflictionType == "burn" then
            targetIdentifiers[identifier] = true
        end
    end

    return targetIdentifiers
end

local function GetBastionBattery(armor)
    if armor == nil or armor.OwnInventory == nil then
        return nil
    end

    local battery = armor.OwnInventory.GetItemAt(BASTION_BATTERY_SLOT)
    if battery == nil or not battery.HasTag("mobilebattery") or battery.Condition <= 0 then
        return nil
    end

    return battery
end

local function GetArchonBattery(armor)
    if armor == nil or armor.OwnInventory == nil then
        return nil
    end

    local battery = armor.OwnInventory.GetItemAt(ARCHON_BATTERY_SLOT)
    if battery == nil or not battery.HasTag("mobilebattery") or battery.Condition <= 0 then
        return nil
    end

    return battery
end

local function MatchesTargetIdentifier(input, required)
    if type(required) == "string" then
        return input == required
    end

    if type(required) == "table" then
        return required[input] == true
    end

    return false
end

local function GetAfflictionIdentity(affliction)
    if affliction == nil or affliction.Prefab == nil then
        return "", ""
    end

    local identifier = string.lower(tostring(affliction.Prefab.Identifier.Value))
    local afflictionType = string.lower(tostring(affliction.Prefab.AfflictionType.Value))
    return identifier, afflictionType
end

local function SelectAfflictionForArmor(afflictions, data)
    local fieldIdentifier = data.aicProbabilityFieldIdentifier
    if fieldIdentifier == BASTION_IDENTIFIER or fieldIdentifier == ARCHON_IDENTIFIER then
        local weightedDamage = 0

        for affliction in afflictions do
            local identifier, afflictionType = GetAfflictionIdentity(affliction)
            local strength = math.max(0, affliction.Strength or 0)

            if afflictionType == "damage" and identifier ~= "organdamage" then
                weightedDamage = weightedDamage + strength
            elseif afflictionType == "burn" then
                local burnMultiplier = fieldIdentifier == BASTION_IDENTIFIER and BASTION_BURN_DRAIN_MULTIPLIER or 1
                weightedDamage = weightedDamage + strength * burnMultiplier
            end
        end

        return weightedDamage > 0, { Strength = weightedDamage }
    end

    local targetIdentifiers = data.targetidentifier
    local selectedAffliction = { Strength = 0 }
    local shouldExecute = false

    for affliction in afflictions do
        if targetIdentifiers == "Any" then
            selectedAffliction.Strength = selectedAffliction.Strength + (affliction.Strength or 0)
            shouldExecute = true
        elseif not shouldExecute then
            local identifier, afflictionType = GetAfflictionIdentity(affliction)
            if MatchesTargetIdentifier(identifier, targetIdentifiers) or MatchesTargetIdentifier(afflictionType, targetIdentifiers) then
                selectedAffliction = affliction
                shouldExecute = true
            end
        end
    end

    return shouldExecute, selectedAffliction
end

InstallDamageLimbPatch = function()
    Hook.RemovePatch("Kilo", "Barotrauma.Character", "DamageLimb", Hook.HookMethodType.Before)

    Hook.Patch("aic_vce_aggregate_damage", "Barotrauma.Character", "DamageLimb", function(instance, ptable)
        local targetLimb = ptable["hitLimb"]
        if targetLimb == nil then
            return
        end

        local afflictions = ptable["afflictions"]
        local rawPenetration = ptable["penetration"]
        if afflictions == nil or type(rawPenetration) ~= "number" then
            return
        end

        local penetrationLevel = math.floor((rawPenetration + 0.00001) * 10)
        local targetCharacter = targetLimb.character
        local outerCloth, innerPlate = VCE.ArmorSystem.getArmor(targetCharacter, targetLimb)
        local clothData = outerCloth and VCE.ArmorConfigs[outerCloth.Prefab.Identifier.Value] or nil
        local plateData = innerPlate and VCE.ArmorConfigs[innerPlate.Prefab.Identifier.Value] or nil

        if outerCloth ~= nil and clothData == nil then
            return
        end

        if clothData == nil and plateData == nil then
            return
        end

        local executeCloth, clothAffliction = false, { Strength = 0 }
        local executePlate, plateAffliction = false, { Strength = 0 }

        if clothData ~= nil and clothData.targetidentifier ~= nil then
            executeCloth, clothAffliction = SelectAfflictionForArmor(afflictions, clothData)
        end
        if plateData ~= nil and plateData.targetidentifier ~= nil then
            executePlate, plateAffliction = SelectAfflictionForArmor(afflictions, plateData)
        end

        if executePlate and (clothData == nil or not clothData.isPlateCarrier) then
            executePlate = false
        end
        if not executeCloth and not executePlate then
            return
        end

        local damageMultiplier = 1.0
        local continue = true

        if executePlate then
            damageMultiplier, penetrationLevel, continue = VCE.ArmorSystem.PlateMain(
                plateData,
                innerPlate,
                penetrationLevel,
                damageMultiplier,
                plateAffliction,
                targetCharacter,
                targetLimb,
                rawPenetration)
            ptable.PreventExecution = not continue
            if not continue then
                return
            end
        end

        if executeCloth and clothData.protectionarea[targetLimb.type] then
            damageMultiplier, penetrationLevel, continue = VCE.ArmorSystem.PlateMain(
                clothData,
                outerCloth,
                penetrationLevel,
                damageMultiplier,
                clothAffliction,
                targetCharacter,
                targetLimb,
                rawPenetration)
            ptable.PreventExecution = not continue
            if not continue then
                return
            end
        end

        local decreasedPenetration = rawPenetration - (penetrationLevel / 10)
        ptable["penetration"] = Single(rawPenetration + 0.00001 - decreasedPenetration)
        ptable["damageMultiplier"] = Single(ptable["damageMultiplier"] * damageMultiplier)
    end, Hook.HookMethodType.Before)
end

FieldSystem.Register({
    identifier = BASTION_IDENTIFIER,
    name = "Bastion Meissner Shield",
    ricochetchance = 1,
    targetidentifier = BuildBastionTargetIdentifiers(),
    protectionarea = {
        [LimbType.Head] = true,
        [LimbType.Torso] = true,
        [LimbType.Waist] = true,
        [LimbType.LeftArm] = true,
        [LimbType.LeftForearm] = true,
        [LimbType.LeftHand] = true,
        [LimbType.RightArm] = true,
        [LimbType.RightForearm] = true,
        [LimbType.RightHand] = true,
        [LimbType.LeftThigh] = true,
        [LimbType.RightThigh] = true,
        [LimbType.LeftLeg] = true,
        [LimbType.RightLeg] = true,
        [LimbType.LeftFoot] = true,
        [LimbType.RightFoot] = true
    },
    canBlock = function(armor)
        if not armor.HasTag(BASTION_SHIELD_ENABLED_TAG) then
            return false
        end

        if GetBastionBattery(armor) == nil then
            return false
        end

        return true
    end,
    onBlock = function(armor, character, limb, affliction, penetrationLevel, rawPenetration)
        local battery = GetBastionBattery(armor)
        if battery ~= nil then
            local weightedDamage = affliction and affliction.Strength or 0
            local penetration = math.max(0, tonumber(rawPenetration) or ((penetrationLevel or 0) / 10))
            local batteryDrain = math.max(0, weightedDamage * penetration)
            battery.Condition = math.max(0, battery.Condition - batteryDrain)
        end
    end
})

FieldSystem.Register({
    identifier = ARCHON_IDENTIFIER,
    name = "Archon Threshold Shield",
    ricochetchance = 1,
    targetidentifier = BuildBastionTargetIdentifiers(),
    protectionarea = {
        [LimbType.Head] = true,
        [LimbType.Torso] = true,
        [LimbType.Waist] = true,
        [LimbType.LeftArm] = true,
        [LimbType.LeftForearm] = true,
        [LimbType.LeftHand] = true,
        [LimbType.RightArm] = true,
        [LimbType.RightForearm] = true,
        [LimbType.RightHand] = true,
        [LimbType.LeftThigh] = true,
        [LimbType.RightThigh] = true,
        [LimbType.LeftLeg] = true,
        [LimbType.RightLeg] = true,
        [LimbType.LeftFoot] = true,
        [LimbType.RightFoot] = true
    },
    canBlock = function(armor, character, limb, affliction, penetrationLevel, rawPenetration)
        if not armor.HasTag(ARCHON_SHIELD_ENABLED_TAG) then
            return false
        end

        if GetArchonBattery(armor) == nil then
            return false
        end

        local totalDamage = affliction and affliction.Strength or 0
        local penetration = math.max(0, tonumber(rawPenetration) or ((penetrationLevel or 0) / 10))
        if totalDamage < ARCHON_MIN_DAMAGE then
            return false
        end

        if penetration < ARCHON_MIN_RAW_PENETRATION then
            return false
        end

        return true
    end,
    onBlock = function(armor)
        local battery = GetArchonBattery(armor)
        if battery == nil then
            return
        end

        battery.Condition = math.max(0, battery.Condition - ARCHON_BATTERY_DRAIN)
    end
})

Initialize(MAX_INITIALIZATION_RETRIES)
