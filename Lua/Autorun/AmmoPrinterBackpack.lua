if CLIENT and Game.IsMultiplayer then
    return
end

local PRINTER_IDENTIFIER = "aic_wardens_ammo_printer_backpack"
local TUNGSTEN_IDENTIFIER = "aic_material_tungsten_steel"
local TARGET_SLOT = 0
local SOURCE_SLOT = 1
local UPDATE_INTERVAL = 0.5
local TRANSFER_AMOUNT = 12

local nextUpdateTime = 0

local function GetIdentifier(item)
    if item == nil or item.Prefab == nil or item.Prefab.Identifier == nil then
        return ""
    end
    return string.lower(tostring(item.Prefab.Identifier.Value))
end

local function IsPrinter(item)
    return GetIdentifier(item) == PRINTER_IDENTIFIER
end

local function IsMagazine(item)
    return item ~= nil and item.HasTag("aic_wardens_weaponmag")
end

local function IsSupply(item)
    return IsMagazine(item) or GetIdentifier(item) == TUNGSTEN_IDENTIFIER
end

local function TransferCondition(source, target, maximumAmount)
    if source == nil or target == nil or source.Condition <= 0 then
        return 0
    end

    local targetSpace = target.MaxCondition - target.Condition
    if targetSpace <= 0 then
        return 0
    end

    local amount = math.min(maximumAmount, source.Condition, targetSpace)
    source.Condition = math.max(0, source.Condition - amount)
    target.Condition = math.min(target.MaxCondition, target.Condition + amount)
    return amount
end

local function UpdatePrinter(printer)
    local inventory = printer.OwnInventory
    if inventory == nil then
        return
    end

    local source = inventory.GetItemAt(SOURCE_SLOT)
    if IsSupply(source) then
        TransferCondition(source, printer, TRANSFER_AMOUNT)
        if GetIdentifier(source) == TUNGSTEN_IDENTIFIER and source.Condition <= 0 then
            Entity.Spawner.AddItemToRemoveQueue(source)
        end
    end

    local target = inventory.GetItemAt(TARGET_SLOT)
    if IsMagazine(target) then
        TransferCondition(printer, target, TRANSFER_AMOUNT)
    end
end

Hook.Add("think", "aic_ammo_printer_backpack_update", function()
    local now = Timer.GetTime()
    if now < nextUpdateTime then
        return
    end
    nextUpdateTime = now + UPDATE_INTERVAL

    for item in Item.ItemList do
        if IsPrinter(item) then
            UpdatePrinter(item)
        end
    end
end)
