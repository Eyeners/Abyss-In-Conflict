if CLIENT and Game.IsMultiplayer then return end

local REDSTAR_IDENTIFIER = "aic_gdf_redstar_plasma_rifle"
local REDSTAR_BATTERY_TAG = "aic_gdf_redstar_plasma_battery"
local ANY_INVENTORY_SLOT = { InvSlotType.Any }

local function IsRedStar(item)
    return item ~= nil and not item.Removed and
        item.Prefab ~= nil and
        string.lower(tostring(item.Prefab.Identifier)) == REDSTAR_IDENTIFIER
end

local function FindChargedBattery(inventory)
    if inventory == nil then
        return nil
    end

    for candidate in inventory.AllItemsMod do
        if candidate ~= nil and not candidate.Removed then
            if candidate.HasTag(REDSTAR_BATTERY_TAG) and candidate.Condition > 0 then
                return candidate
            end

            local nestedBattery = FindChargedBattery(candidate.OwnInventory)
            if nestedBattery ~= nil then
                return nestedBattery
            end
        end
    end

    return nil
end

local function RemoveBeamCapacitor(item)
    if item == nil or item.OwnInventory == nil then
        return
    end

    local containedItem = item.OwnInventory.GetItemAt(0)
    if containedItem == nil or not containedItem.HasTag("aic_gdf_redstar_beam_capacitor") then
        return
    end

    item.OwnInventory.RemoveItem(containedItem)
    Entity.Spawner.AddEntityToRemoveQueue(containedItem)
end

local function TryReloadBot(item, owner, attemptsRemaining)
    if not IsRedStar(item) or item.OwnInventory == nil or
        owner == nil or owner.Removed or owner.Inventory == nil or not owner.IsBot or
        item.GetRootInventoryOwner() ~= owner then
        return
    end

    local current = item.OwnInventory.GetItemAt(0)
    if current ~= nil and current.HasTag(REDSTAR_BATTERY_TAG) then
        if current.Condition > 0 then return end
        -- Only replace a depleted battery when the bot actually has a usable spare.
        if FindChargedBattery(owner.Inventory) == nil then return end
        if not owner.Inventory.TryPutItem(current, owner, ANY_INVENTORY_SLOT, true, true) then
            current.Drop(owner, true, true)
        end
        current = item.OwnInventory.GetItemAt(0)
    end
    if current ~= nil then
        if attemptsRemaining > 0 then
            Timer.Wait(function()
                TryReloadBot(item, owner, attemptsRemaining - 1)
            end, 100)
        end
        return
    end

    local battery = FindChargedBattery(owner.Inventory)
    if battery ~= nil then
        item.OwnInventory.TryPutItem(battery, 0, false, true, owner, true, false, true)
    end
end

Hook.Add("aic_redstar_eject_battery", "aic_redstar_eject_battery", function(effect, deltaTime, item, targets)
    if not IsRedStar(item) or item.OwnInventory == nil then
        return
    end

    local battery = item.OwnInventory.GetItemAt(0)
    if battery == nil or not battery.HasTag(REDSTAR_BATTERY_TAG) then
        return
    end

    local owner = item.GetRootInventoryOwner()
    if owner ~= nil and
        LuaUserData.IsTargetType(owner, "Barotrauma.Character") and
        owner.Inventory ~= nil and
        owner.Inventory.TryPutItem(battery, owner, ANY_INVENTORY_SLOT, true, true) then
        return
    end

    battery.Drop(owner, true, true)
end)

-- Normal fire can exhaust a battery before the next beam cycle. Defer the inventory
-- change until the current shot's contained-item effects have finished iterating.
local pendingReload = setmetatable({}, { __mode = "k" })
Hook.Add("aic_redstar_battery_used", "aic_redstar_battery_used", function(effect, deltaTime, battery)
    -- Each shot costs 3; tolerate LuaHook running before the condition subtraction.
    if battery == nil or battery.Removed or battery.Condition > 3 then return end
    local item = battery.ParentInventory and battery.ParentInventory.Owner
    if item == nil or not LuaUserData.IsTargetType(item, "Barotrauma.Item") or
        not IsRedStar(item) or pendingReload[item] then return end
    local owner = item.GetRootInventoryOwner()
    if owner == nil or not LuaUserData.IsTargetType(owner, "Barotrauma.Character") or not owner.IsBot then return end
    pendingReload[item] = true
    Timer.Wait(function()
        pendingReload[item] = nil
        if battery.Removed or battery.Condition > 0 then return end
        TryReloadBot(item, owner, 2)
    end, 0)
end)

Hook.Add("aic_redstar_reload_after_beam", "aic_redstar_reload_after_beam", function(effect, deltaTime, item)
    if not IsRedStar(item) then
        return
    end

    local owner = item.GetRootInventoryOwner()
    if owner == nil or
        not LuaUserData.IsTargetType(owner, "Barotrauma.Character") or
        not owner.IsBot then
        return
    end

    -- Do this in the same frame so combat AI never observes an empty primary slot and switches to retreat mode.
    RemoveBeamCapacitor(item)
    TryReloadBot(item, owner, 2)
end)
