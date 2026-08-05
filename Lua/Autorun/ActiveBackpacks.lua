if SERVER then
    return
end

local BACKPACK_TAG = "aic_self_destruct_backpack"
local BUTTON_LABEL = "启动自爆"

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

    local backpack = character.GetEquippedItem(BACKPACK_TAG, InvSlotType.Bag)
    if backpack == nil then
        return
    end

    local customInterface = backpack.GetComponentString("CustomInterface")
    if customInterface ~= nil and customInterface.DrawHudWhenEquipped then
        ActivateTickBox(customInterface, BUTTON_LABEL)
    end
end)
