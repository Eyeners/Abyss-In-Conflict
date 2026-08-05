if SERVER then return end

local zoomProfiles = {
    ["aic_scope_zoom2x"] = {
        targetOffset = 620,
        smooth = 0.2
    },
    ["aic_scope_zoom2x_builtin"] = {
        targetOffset = 540,
        smooth = 0.18
    }
}

local function lerp(a, b, t)
    return a + (b - a) * t
end

local function findZoomProfile(item)
    if item == nil then
        return nil, nil
    end

    for tag, profile in pairs(zoomProfiles) do
        if item.HasTag(tag) then
            return tag, profile
        end

        if item.ownInventory ~= nil and item.ownInventory.FindItemByTag(tag, true) ~= nil then
            return tag, profile
        end
    end

    return nil, nil
end

local function getHeldItem(character)
    if character == nil or character.Inventory == nil then
        return nil
    end

    local rightHand = character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand)
    local leftHand = character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand)
    return rightHand or leftHand
end

Hook.Patch("Barotrauma.Character", "ControlLocalPlayer", function(instance, ptable)
    if Screen.Selected == nil or Screen.Selected.Cam == nil then
        return
    end

    local character = instance
    if character == nil
        or character.AnimController == nil
        or not character.AnimController.IsAiming then
        return
    end

    local heldItem = getHeldItem(character)
    if heldItem == nil then
        return
    end

    local tag, profile = findZoomProfile(heldItem)
    if tag == nil then
        return
    end

    Screen.Selected.Cam.OffsetAmount = lerp(Screen.Selected.Cam.OffsetAmount, profile.targetOffset, profile.smooth)
end, Hook.HookMethodType.After)
