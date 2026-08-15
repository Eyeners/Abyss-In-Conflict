if SERVER then
    return
end

local BACKPACK_ACTIONS = {
    { tag = "aic_self_destruct_backpack", label = "启动自爆" },
    { tag = "aic_multifunction_medical_backpack", label = "释放医疗烟雾" }
}

LuaUserData.MakeFieldAccessible(
    Descriptors["Barotrauma.Items.Components.CustomInterface"],
    "uiElements"
)

local function ActivateTickBox(customInterface, label)
    local index = 1
    for element in customInterface.uiElements do
        if LuaUserData.IsTargetType(element, "Barotrauma.GUITickBox")
            and customInterface.customInterfaceElementList[index].Label == label then
            if not element.Selected then
                element.Selected = true
            end
            return
        end
        index = index + 1
    end
end

Hook.Add("think", "aic_active_backpack_hotkey", function()
    local character = Character.Controlled
    if character == nil then
        return
    end
    if GUI.KeyboardDispatcher.Subscriber ~= nil or GUI.InputBlockingMenuOpen then
        return
    end
    if not PlayerInput.KeyHit(Keys.Q) then
        return
    end

    for _, action in ipairs(BACKPACK_ACTIONS) do
        local backpack = character.GetEquippedItem(action.tag, InvSlotType.Bag)
        if backpack ~= nil then
            local customInterface = backpack.GetComponentString("CustomInterface")
            if customInterface ~= nil and customInterface.DrawHudWhenEquipped then
                ActivateTickBox(customInterface, action.label)
            end
            return
        end
    end
end)
