if SERVER then return end

local WHEEL_STEP = 0.07
local TARGET_OFFSET = 900
-- Ordered: an installed scope takes precedence over the built-in optic.
local profiles = {
    { tag = "aic_scope_zoom3x_builtin", min = 0, max = 0.55, default = 0.28 },
    { tag = "aic_scope_zoom2x", min = 0, max = 0.4, default = 0.2 },
    { tag = "aic_scope_zoom2x_builtin", min = 0, max = 0.35, default = 0.18 }
}
local zoomStates = setmetatable({}, { __mode = "k" })

local function findProfile(item, includeContents)
    if not item or item.Removed or item.Condition <= 0 then return nil end
    for _, profile in ipairs(profiles) do
        if item.HasTag(profile.tag) or (includeContents and item.ownInventory
            and item.ownInventory.FindItemByTag(profile.tag, true)) then
            return profile
        end
    end
end

local function selectSource(character)
    local inventory = character.Inventory
    -- Grapple aiming reserves the wheel, including when a zoom helmet is equipped.
    for _, slot in ipairs({ InvSlotType.RightHand, InvSlotType.LeftHand }) do
        local item = inventory.GetItemInLimbSlot(slot)
        if item and item.Prefab.Identifier.Value == "aic_grapple_gun" then return end
    end
    -- Select one source only: weapon/accessory first, helmet as a fallback.
    for _, slot in ipairs({ InvSlotType.RightHand, InvSlotType.LeftHand }) do
        local item = inventory.GetItemInLimbSlot(slot)
        local profile = findProfile(item, true)
        if profile then return item, profile end
    end
    local helmet = inventory.GetItemInLimbSlot(InvSlotType.Head)
    local profile = findProfile(helmet, false)
    if profile then return helmet, profile end
end

Hook.Patch("Barotrauma.Character", "ControlLocalPlayer", function(character)
    if not character or character ~= Character.Controlled or character.Removed or character.IsDead
        or not character.Inventory or not character.AnimController
        or not character.AnimController.IsAiming or not Screen.Selected or not Screen.Selected.Cam then return end
    local source, profile = selectSource(character)
    if not source then return end
    local state = zoomStates[source]
    if not state then state = {}; zoomStates[source] = state end
    local value = state[profile.tag] or profile.default
    if GUI.KeyboardDispatcher.Subscriber == nil and not GUI.InputBlockingMenuOpen and not GUI.MouseOn then
        if PlayerInput.MouseWheelUpClicked() then
            value = value - WHEEL_STEP
        elseif PlayerInput.MouseWheelDownClicked() then
            value = value + WHEEL_STEP
        end
    end
    value = math.max(profile.min, math.min(profile.max, value))
    state[profile.tag] = value
    local cam = Screen.Selected.Cam
    cam.OffsetAmount = cam.OffsetAmount + (TARGET_OFFSET - cam.OffsetAmount) * value
end, Hook.HookMethodType.After)

Hook.Add("roundEnd", "AIC.ScopeZoom.Reset", function()
    zoomStates = setmetatable({}, { __mode = "k" })
end)
