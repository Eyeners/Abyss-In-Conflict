if SERVER then return end

LuaUserData.MakeFieldAccessible(Descriptors["Barotrauma.Items.Components.Wearable"], "wearableSprites")

local BASTION_IDENTIFIER = "aic_wardens_bastion_experimental_powerarmor"
local SHIELD_ENABLED_TAG = "aic_bastion_shield_on"
local BATTERY_SLOT = 2

-- The waist remains the T2 sprite, so index 9 is intentionally skipped.
local SPRITE_INDICES = { 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 }

local NORMAL_RECTS = {
    { 1508, 121, 173, 167 },
    { 628, 338, 326, 577 },
    { 1310, 791, 82, 105 },
    { 1006, 791, 82, 105 },
    { 1264, 91, 144, 255 },
    { 977, 91, 144, 255 },
    { 1288, 500, 97, 169 },
    { 1002, 500, 97, 169 },
    { 441, 178, 163, 325 },
    { 149, 178, 163, 325 },
    { 451, 702, 143, 320 },
    { 165, 702, 143, 320 },
    { 960, 957, 161, 97 },
    { 1264, 955, 161, 97 }
}

local SHIELD_RECTS = {
    { 1463, 1176, 173, 167 },
    { 617, 1383, 326, 577 },
    { 1302, 1829, 82, 105 },
    { 1000, 1829, 82, 105 },
    { 1277, 1136, 144, 255 },
    { 969, 1136, 144, 255 },
    { 1285, 1534, 97, 169 },
    { 990, 1534, 97, 169 },
    { 436, 1228, 163, 325 },
    { 158, 1228, 163, 325 },
    { 436, 1739, 143, 318 },
    { 149, 1739, 143, 318 },
    { 952, 1994, 161, 99 },
    { 1263, 1994, 161, 99 }
}

local lastShieldState = setmetatable({}, { __mode = "k" })

local function hasChargedBattery(item)
    if item.OwnInventory == nil then
        return false
    end

    local battery = item.OwnInventory.GetItemAt(BATTERY_SLOT)
    return battery ~= nil and battery.HasTag("mobilebattery") and battery.Condition > 0
end

local function shouldShowShield(item)
    return item.HasTag(SHIELD_ENABLED_TAG) and hasChargedBattery(item)
end

local function updateShieldSprites(item, force)
    if item == nil or item.Removed then
        return
    end

    local identifier = string.lower(tostring(item.Prefab.Identifier.Value))
    if identifier ~= BASTION_IDENTIFIER then
        return
    end

    local shieldVisible = shouldShowShield(item)
    if not force and lastShieldState[item] == shieldVisible then
        return
    end

    local wearable = item.GetComponentString("Wearable")
    if wearable == nil then
        return
    end

    local sourceRects = shieldVisible and SHIELD_RECTS or NORMAL_RECTS
    for index, spriteIndex in ipairs(SPRITE_INDICES) do
        local wearableSprite = wearable.wearableSprites[spriteIndex]
        if wearableSprite ~= nil and wearableSprite.Sprite ~= nil then
            local rect = sourceRects[index]
            wearableSprite.Sprite.SourceRect = Rectangle(rect[1], rect[2], rect[3], rect[4])
        end
    end

    lastShieldState[item] = shieldVisible
end

Hook.Add("aic_bastion_shield_sprite_update", "aic_bastion_shield_sprite_update", function(effect, deltaTime, item)
    updateShieldSprites(item, false)
end)

Hook.Add("item.equip", "aic_bastion_shield_sprite_equip", function(item)
    updateShieldSprites(item, true)
end)
