local ARCHON_IDENTIFIER = "aic_practitioner_archon_heavy_armor"
local ARCHON_SHIELD_TAG = "aic_archon_shield_on"
local ARCHON_BOOSTER_TAG = "aic_archon_booster_on"
local BASTION_IDENTIFIER = "aic_wardens_bastion_experimental_powerarmor"
local BASTION_SHIELD_TAG = "aic_bastion_shield_on"
local BASTION_BOOSTER_TAG = "aic_bastion_booster_on"

local function IsExpectedArmor(item, expectedIdentifier)
    if item == nil or item.Removed or item.Prefab == nil then
        return false
    end

    return string.lower(tostring(item.Prefab.Identifier.Value)) == expectedIdentifier
end

local function SyncTags(item)
    if not SERVER or not Game.IsMultiplayer then
        return
    end

    local property = item.SerializableProperties[Identifier("Tags")]
    if property ~= nil then
        Networking.CreateEntityEvent(item, Item.ChangePropertyEventData(property, item))
    end
end

local function SetModeTag(item, expectedIdentifier, tag, enabled)
    if not IsExpectedArmor(item, expectedIdentifier) then
        return
    end

    if enabled then
        if not item.HasTag(tag) then
            item.AddTag(tag)
            SyncTags(item)
        end
    elseif item.HasTag(tag) then
        item.RemoveTag(tag)
        SyncTags(item)
    end
end

Hook.Add("aic_archon_shield_enable", "aic_archon_shield_enable", function(effect, deltaTime, item)
    SetModeTag(item, ARCHON_IDENTIFIER, ARCHON_SHIELD_TAG, true)
end)

Hook.Add("aic_archon_shield_disable", "aic_archon_shield_disable", function(effect, deltaTime, item)
    SetModeTag(item, ARCHON_IDENTIFIER, ARCHON_SHIELD_TAG, false)
end)

Hook.Add("aic_archon_booster_enable", "aic_archon_booster_enable", function(effect, deltaTime, item)
    SetModeTag(item, ARCHON_IDENTIFIER, ARCHON_BOOSTER_TAG, true)
end)

Hook.Add("aic_archon_booster_disable", "aic_archon_booster_disable", function(effect, deltaTime, item)
    SetModeTag(item, ARCHON_IDENTIFIER, ARCHON_BOOSTER_TAG, false)
end)

Hook.Add("aic_bastion_shield_enable", "aic_bastion_shield_enable", function(effect, deltaTime, item)
    SetModeTag(item, BASTION_IDENTIFIER, BASTION_SHIELD_TAG, true)
end)

Hook.Add("aic_bastion_shield_disable", "aic_bastion_shield_disable", function(effect, deltaTime, item)
    SetModeTag(item, BASTION_IDENTIFIER, BASTION_SHIELD_TAG, false)
end)

Hook.Add("aic_bastion_booster_enable", "aic_bastion_booster_enable", function(effect, deltaTime, item)
    SetModeTag(item, BASTION_IDENTIFIER, BASTION_BOOSTER_TAG, true)
end)

Hook.Add("aic_bastion_booster_disable", "aic_bastion_booster_disable", function(effect, deltaTime, item)
    SetModeTag(item, BASTION_IDENTIFIER, BASTION_BOOSTER_TAG, false)
end)
