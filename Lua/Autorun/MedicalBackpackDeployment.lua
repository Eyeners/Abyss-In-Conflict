if CLIENT and Game.IsMultiplayer then
    return
end

local BACKPACK_IDENTIFIER = "aic_multifunction_medical_backpack_test"
local TABLE_IDENTIFIER = "aic_multifunction_medical_autopsy_table_test"
local FALLBACK_INTERVAL = 0.5
local TRANSFORM_DELAY_MS = 100

local pending = {}
local nextFallbackTime = 0

local function GetIdentifier(item)
    if item == nil or item.Prefab == nil or item.Prefab.Identifier == nil then
        return ""
    end
    return string.lower(tostring(item.Prefab.Identifier.Value))
end

local function GetItemContainers(item)
    local containers = {}
    for component in item.Components do
        if LuaUserData.IsTargetType(component, "Barotrauma.Items.Components.ItemContainer") then
            table.insert(containers, component)
        end
    end
    return containers
end

local function MoveInventory(sourceInventory, targetInventory)
    if sourceInventory == nil or targetInventory == nil then
        return false
    end

    local contents = {}
    for containedItem in sourceInventory.AllItemsMod do
        table.insert(contents, {
            item = containedItem,
            slot = sourceInventory.FindIndex(containedItem)
        })
    end

    local moved = {}
    for _, entry in ipairs(contents) do
        if not targetInventory.TryPutItem(entry.item, entry.slot, false, true, nil, true, true, true) then
            for i = #moved, 1, -1 do
                local rollbackEntry = moved[i]
                sourceInventory.TryPutItem(rollbackEntry.item, rollbackEntry.slot, false, true, nil, true, true, true)
            end
            return false
        end
        table.insert(moved, entry)
    end
    return true
end

local function SpawnReplacement(sourceItem, prefabIdentifier, sourceInventory, targetContainerIndex, attachToWall)
    local prefab = ItemPrefab.GetItemPrefab(prefabIdentifier)
    if prefab == nil then
        print("[AIC] Missing medical backpack replacement prefab: " .. prefabIdentifier)
        pending[sourceItem] = nil
        return
    end

    local sourceSubmarine = sourceItem.Submarine
    local spawnPosition = sourceSubmarine ~= nil and sourceItem.Position or sourceItem.WorldPosition
    -- Deploying or packing creates a fresh state, so the medical system starts fully charged.
    local condition = 100
    local quality = sourceItem.Quality
    local parentInventory = sourceItem.ParentInventory
    local parentSlot = parentInventory ~= nil and parentInventory.FindIndex(sourceItem) or -1

    local function OnReplacementSpawned(replacement)
        if replacement == nil or replacement.Removed then
            pending[sourceItem] = nil
            return
        end
        pending[replacement] = true

        if sourceItem == nil or sourceItem.Removed then
            Entity.Spawner.AddItemToRemoveQueue(replacement)
            pending[replacement] = nil
            pending[sourceItem] = nil
            return
        end

        local targetContainers = GetItemContainers(replacement)
        local targetContainer = targetContainers[targetContainerIndex]
        if targetContainer == nil or not MoveInventory(sourceInventory, targetContainer.Inventory) then
            print("[AIC] Failed to transfer multifunction medical backpack inventory")
            Entity.Spawner.AddItemToRemoveQueue(replacement)
            pending[replacement] = nil
            pending[sourceItem] = nil
            return
        end

        if parentInventory ~= nil then
            parentInventory.RemoveItem(sourceItem)
            if parentSlot >= 0 then
                parentInventory.TryPutItem(replacement, parentSlot, false, false, nil, true, true, true)
            end
        end

        if attachToWall then
            local replacementHoldable = replacement.GetComponentString("Holdable")
            if replacementHoldable ~= nil then
                replacementHoldable.AttachToWall()
            end
        end

        Entity.Spawner.AddItemToRemoveQueue(sourceItem)
        pending[replacement] = nil
        pending[sourceItem] = nil
    end

    if sourceSubmarine ~= nil then
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, sourceSubmarine, condition, quality, OnReplacementSpawned)
    else
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, condition, quality, OnReplacementSpawned)
    end
end

local function DeployBackpack(backpack)
    local sourceContainers = GetItemContainers(backpack)
    if sourceContainers[1] == nil then
        pending[backpack] = nil
        return
    end
    SpawnReplacement(backpack, TABLE_IDENTIFIER, sourceContainers[1].Inventory, 2, true)
end

local function PackTable(tableItem)
    local sourceContainers = GetItemContainers(tableItem)
    local inputContainer = sourceContainers[1]
    local outputContainer = sourceContainers[2]
    if inputContainer == nil or outputContainer == nil then
        pending[tableItem] = nil
        return
    end

    if not inputContainer.Inventory.IsEmpty() then
        local holdable = tableItem.GetComponentString("Holdable")
        if holdable ~= nil then
            holdable.AttachToWall()
        end
        pending[tableItem] = nil
        return
    end

    SpawnReplacement(tableItem, BACKPACK_IDENTIFIER, outputContainer.Inventory, 1, false)
end

local function CheckTransformation(item)
    if item == nil or item.Removed or pending[item] then
        return
    end

    local identifier = GetIdentifier(item)
    if identifier ~= BACKPACK_IDENTIFIER and identifier ~= TABLE_IDENTIFIER then
        return
    end

    local holdable = item.GetComponentString("Holdable")
    if holdable == nil then
        return
    end

    if identifier == BACKPACK_IDENTIFIER and holdable.Attached then
        pending[item] = true
        DeployBackpack(item)
    elseif identifier == TABLE_IDENTIFIER and not holdable.Attached then
        pending[item] = true
        PackTable(item)
    end
end

local function ScheduleCheck(item)
    local identifier = GetIdentifier(item)
    if identifier ~= BACKPACK_IDENTIFIER and identifier ~= TABLE_IDENTIFIER then
        return
    end
    Timer.Wait(function()
        CheckTransformation(item)
    end, TRANSFORM_DELAY_MS)
end

Hook.Add("item.interact", "aic_medical_backpack_transform_interact", function(item)
    ScheduleCheck(item)
end)

Hook.Add("item.drop", "aic_medical_backpack_transform_drop", function(item)
    ScheduleCheck(item)
end)

Hook.Add("think", "aic_medical_backpack_transform_fallback", function()
    local now = Timer.GetTime()
    if now < nextFallbackTime then
        return
    end
    nextFallbackTime = now + FALLBACK_INTERVAL

    for item in Item.ItemList do
        CheckTransformation(item)
    end
end)

Hook.Add("roundEnd", "aic_medical_backpack_transform_cleanup", function()
    pending = {}
end)
