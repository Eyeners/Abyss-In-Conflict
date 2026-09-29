local FIELD_BY_DEVICE = {
    aic_rectangular_gravity_field_device = "aic_rectangular_gravity_field",
    aic_antigravity_grenade = "aic_antigravity_field"
}
local FIELD_VERTICAL_OFFSET = 150

local activatedDevices = setmetatable({}, { __mode = "k" })
local roundGeneration = 0

local function IdentifierValue(item)
    if item == nil or item.Prefab == nil then return "" end
    return string.lower(tostring(item.Prefab.Identifier.Value))
end

local function FreezeDevice(item)
    local body = item ~= nil and item.body or nil
    if body == nil then return end

    pcall(function() body.LinearVelocity = Vector2(0, 0) end)
    pcall(function() body.AngularVelocity = 0 end)
    -- Disabling the source body leaves the activated device sprite fixed while the static field handles gameplay.
    pcall(function() body.Enabled = false end)
end

Hook.Add("aic_rectangular_gravity_field_activate", "AIC.RectangularGravityField.Activate", function(effect, deltaTime, item)
    if item == nil or item.Removed then return end
    local fieldIdentifier = FIELD_BY_DEVICE[IdentifierValue(item)]
    if fieldIdentifier == nil then return end
    if activatedDevices[item] then return end

    FreezeDevice(item)
    if CLIENT and Game.IsMultiplayer then
        activatedDevices[item] = true
        return
    end

    local prefab = ItemPrefab.GetItemPrefab(fieldIdentifier)
    if prefab == nil then
        print("[AIC Rectangular Gravity Field] Missing field prefab: " .. fieldIdentifier)
        return
    end

    activatedDevices[item] = true
    local submarine = item.Submarine
    local sourcePosition = submarine ~= nil and item.Position or item.WorldPosition
    local spawnPosition = Vector2(sourcePosition.X, sourcePosition.Y + FIELD_VERTICAL_OFFSET)
    local generation = roundGeneration

    local function OnSpawned(field)
        if field == nil or field.Removed then return end
        if generation ~= roundGeneration then
            Entity.Spawner.AddItemToRemoveQueue(field)
        end
    end

    if submarine ~= nil then
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, submarine, nil, nil, OnSpawned)
    else
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, nil, nil, OnSpawned)
    end
end)

Hook.Add("roundEnd", "AIC.RectangularGravityField.Reset", function()
    roundGeneration = roundGeneration + 1
    activatedDevices = setmetatable({}, { __mode = "k" })
end)
