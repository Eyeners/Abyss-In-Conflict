local ARCHON_IDENTIFIER = "aic_practitioner_archon_heavy_armor"
local ARCHON_SHIELD_TAG = "aic_archon_shield_on"
local ARCHON_BOOSTER_TAG = "aic_archon_booster_on"
local BASTION_IDENTIFIER = "aic_wardens_bastion_experimental_powerarmor"
local BASTION_SHIELD_TAG = "aic_bastion_shield_on"
local BASTION_BOOSTER_TAG = "aic_bastion_booster_on"
local BASTION_BATTERY_SLOT = 2
local FORESIGHT_IDENTIFIER = "aic_foresight_t3_armor"
local FORESIGHT_SHIELD_TAG = "aic_foresight_nanocluster_shield_on"
local FORESIGHT_BOOSTER_TAG = "aic_foresight_booster_on"
local BASTION_SOUND_RANGE = 2000
local BASTION_SOUND_EVENTS = {
    impact = "aic_bastion_shield_impact_sound",
    breakage = "aic_bastion_shield_break_sound",
    online = "aic_bastion_shield_online_sound"
}
local bastionShieldSounds = {}

local function LoadBastionShieldSounds()
    if not CLIENT or bastionShieldSounds.impact ~= nil then
        return
    end

    local soundDirectory = ST_Lua.Path .. "/卫戍者/音效/"
    bastionShieldSounds.impact = Game.SoundManager.LoadSound(soundDirectory .. "Armor.ogg")
    bastionShieldSounds.breakage = Game.SoundManager.LoadSound(soundDirectory .. "ArmorBreak.ogg")
    bastionShieldSounds.online = Game.SoundManager.LoadSound(soundDirectory .. "电磁盾上线.ogg")
end

local function PlayBastionShieldSound(soundType, position)
    if not CLIENT then
        return
    end

    LoadBastionShieldSounds()
    local sound = bastionShieldSounds[soundType]
    if sound ~= nil then
        SoundPlayer.PlaySound(sound, position, 1, BASTION_SOUND_RANGE, 1)
    end
end

local function EmitBastionShieldSound(soundType, armor)
    if armor == nil or armor.Removed then
        return
    end

    local eventName = BASTION_SOUND_EVENTS[soundType]
    if eventName == nil then
        return
    end

    local position = Vector2(armor.WorldPosition.X, armor.WorldPosition.Y)
    if Game.IsMultiplayer then
        if not SERVER then
            return
        end

        local message = Networking.Start(eventName)
        message.WriteDouble(position.X)
        message.WriteDouble(position.Y)
        Networking.Send(message)
    else
        PlayBastionShieldSound(soundType, position)
    end
end

if CLIENT then
    LoadBastionShieldSounds()
    if Game.IsMultiplayer then
        Networking.Receive(BASTION_SOUND_EVENTS.impact, function(message)
            PlayBastionShieldSound("impact", Vector2(message.ReadDouble(), message.ReadDouble()))
        end)
        Networking.Receive(BASTION_SOUND_EVENTS.breakage, function(message)
            PlayBastionShieldSound("breakage", Vector2(message.ReadDouble(), message.ReadDouble()))
        end)
        Networking.Receive(BASTION_SOUND_EVENTS.online, function(message)
            PlayBastionShieldSound("online", Vector2(message.ReadDouble(), message.ReadDouble()))
        end)
    end
end

AIC = AIC or {}
AIC.BastionShieldSounds = {
    PlayImpact = function(armor)
        EmitBastionShieldSound("impact", armor)
    end,
    PlayBreak = function(armor)
        EmitBastionShieldSound("breakage", armor)
    end,
    PlayOnline = function(armor)
        EmitBastionShieldSound("online", armor)
    end
}

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
        return false
    end

    if enabled then
        if not item.HasTag(tag) then
            item.AddTag(tag)
            SyncTags(item)
            return true
        end
    elseif item.HasTag(tag) then
        item.RemoveTag(tag)
        SyncTags(item)
        return true
    end

    return false
end

local function HasUsableBastionBattery(item)
    if not IsExpectedArmor(item, BASTION_IDENTIFIER) or item.OwnInventory == nil then
        return false
    end

    local battery = item.OwnInventory.GetItemAt(BASTION_BATTERY_SLOT)
    return battery ~= nil and battery.HasTag("mobilebattery") and battery.Condition > 0
end

Hook.Add("aic_archon_shield_enable", "aic_archon_shield_enable", function(effect, deltaTime, item)
    SetModeTag(item, ARCHON_IDENTIFIER, ARCHON_SHIELD_TAG, true)
end)

Hook.Add("aic_archon_shield_disable", "aic_archon_shield_disable", function(effect, deltaTime, item)
    SetModeTag(item, ARCHON_IDENTIFIER, ARCHON_SHIELD_TAG, false)
end)

Hook.Add("aic_foresight_shield_enable", "aic_foresight_shield_enable", function(effect, deltaTime, item)
    SetModeTag(item, FORESIGHT_IDENTIFIER, FORESIGHT_SHIELD_TAG, true)
end)

Hook.Add("aic_foresight_shield_disable", "aic_foresight_shield_disable", function(effect, deltaTime, item)
    SetModeTag(item, FORESIGHT_IDENTIFIER, FORESIGHT_SHIELD_TAG, false)
end)

Hook.Add("aic_foresight_booster_enable", "aic_foresight_booster_enable", function(effect, deltaTime, item)
    SetModeTag(item, FORESIGHT_IDENTIFIER, FORESIGHT_BOOSTER_TAG, true)
end)

Hook.Add("aic_foresight_booster_disable", "aic_foresight_booster_disable", function(effect, deltaTime, item)
    SetModeTag(item, FORESIGHT_IDENTIFIER, FORESIGHT_BOOSTER_TAG, false)
end)

Hook.Add("aic_archon_booster_enable", "aic_archon_booster_enable", function(effect, deltaTime, item)
    SetModeTag(item, ARCHON_IDENTIFIER, ARCHON_BOOSTER_TAG, true)
end)

Hook.Add("aic_archon_booster_disable", "aic_archon_booster_disable", function(effect, deltaTime, item)
    SetModeTag(item, ARCHON_IDENTIFIER, ARCHON_BOOSTER_TAG, false)
end)

Hook.Add("aic_bastion_shield_enable", "aic_bastion_shield_enable", function(effect, deltaTime, item)
    local changed = SetModeTag(item, BASTION_IDENTIFIER, BASTION_SHIELD_TAG, true)
    if changed and HasUsableBastionBattery(item) then
        AIC.BastionShieldSounds.PlayOnline(item)
    end
end)

Hook.Add("aic_bastion_shield_enable_silent", "aic_bastion_shield_enable_silent", function(effect, deltaTime, item)
    SetModeTag(item, BASTION_IDENTIFIER, BASTION_SHIELD_TAG, true)
end)

Hook.Add("aic_bastion_shield_battery_inserted", "aic_bastion_shield_battery_inserted", function(effect, deltaTime, item)
    if IsExpectedArmor(item, BASTION_IDENTIFIER)
        and item.HasTag(BASTION_SHIELD_TAG)
        and HasUsableBastionBattery(item) then
        AIC.BastionShieldSounds.PlayOnline(item)
    end
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
