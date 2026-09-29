local ROUND_IDENTIFIER = "aic_gravity_gun_round"
local FIELD_IDENTIFIER = "aic_gravity_gun_field"
local FIELD_CENTER_HEIGHT = 150
local processed = setmetatable({}, { __mode = "k" })
local generation = 0

Hook.Add("aic_gravity_gun_impact", "AIC.GravityGun.Impact", function(effect, deltaTime, item)
    if CLIENT and Game.IsMultiplayer then return end
    if item == nil or item.Removed or item.Prefab == nil
        or tostring(item.Prefab.Identifier.Value) ~= ROUND_IDENTIFIER or processed[item] then return end
    local prefab = ItemPrefab.GetItemPrefab(FIELD_IDENTIFIER)
    if prefab == nil then
        print("[AIC Gravity Gun] Missing field prefab")
        return
    end
    processed[item] = true
    -- The impact is the lower edge; preserve submarine-local spawn coordinates.
    local origin = item.Submarine ~= nil and item.Position or item.WorldPosition
    local position = Vector2(origin.X, origin.Y + FIELD_CENTER_HEIGHT)
    local currentGeneration = generation
    local function Spawned(field)
        if field == nil or field.Removed then return end
        if currentGeneration ~= generation then Entity.Spawner.AddItemToRemoveQueue(field); return end
        field.HiddenInGame = true
    end
    if item.Submarine ~= nil then
        Entity.Spawner.AddItemToSpawnQueue(prefab, position, item.Submarine, nil, nil, Spawned)
    else
        Entity.Spawner.AddItemToSpawnQueue(prefab, position, nil, nil, Spawned)
    end
end)

Hook.Add("roundEnd", "AIC.GravityGun.Reset", function()
    generation = generation + 1
    processed = setmetatable({}, { __mode = "k" })
end)
