AIC = AIC or {}

local ARMOR_IDENTIFIER = "aic_foresight_t3_armor"
local SHIELD_LIMB_NAME = "AIC Nano Cluster Shield"
local FLASH_EVENT = "aic_nanocluster_shield_flash"
local FLASH_DURATION = 0.25
local FLASH_STEPS = 5

local flashGeneration = setmetatable({}, { __mode = "k" })
local flashUntil = setmetatable({}, { __mode = "k" })

local function FindShieldLimb(character)
    if character == nil or character.Removed or character.AnimController == nil then
        return nil
    end

    for limb in character.AnimController.Limbs do
        if tostring(limb.Name) == SHIELD_LIMB_NAME then
            return limb
        end
    end

    return nil
end

local function HideShieldIfIdle(character)
    if not CLIENT or character == nil then
        return
    end

    if (flashUntil[character] or 0) > Timer.GetTime() then
        return
    end

    local limb = FindShieldLimb(character)
    if limb ~= nil then
        limb.Alpha = 0
    end
end

local function FlashShieldLocal(character, retryCount)
    if not CLIENT or character == nil or character.Removed then
        return
    end

    local limb = FindShieldLimb(character)
    if limb == nil then
        if (retryCount or 0) < 4 then
            Timer.Wait(function()
                FlashShieldLocal(character, (retryCount or 0) + 1)
            end, 50)
        end
        return
    end

    local generation = (flashGeneration[character] or 0) + 1
    flashGeneration[character] = generation
    flashUntil[character] = Timer.GetTime() + FLASH_DURATION
    limb.Alpha = 1

    for step = 1, FLASH_STEPS do
        local currentStep = step
        Timer.Wait(function()
            if character.Removed or flashGeneration[character] ~= generation then
                return
            end

            local currentLimb = FindShieldLimb(character)
            if currentLimb ~= nil then
                currentLimb.Alpha = 1 - currentStep / FLASH_STEPS
            end
        end, FLASH_DURATION * 1000 * currentStep / FLASH_STEPS)
    end
end

local function EmitShieldFlash(character)
    if character == nil or character.Removed then
        return
    end

    if Game.IsMultiplayer then
        if not SERVER then
            return
        end

        local message = Networking.Start(FLASH_EVENT)
        message.WriteUInt16(character.ID)
        Networking.Send(message)
    else
        FlashShieldLocal(character, 0)
    end
end

AIC.NanoClusterShieldVisual = {
    Flash = EmitShieldFlash
}

if CLIENT and Game.IsMultiplayer then
    Networking.Receive(FLASH_EVENT, function(message)
        FlashShieldLocal(Entity.FindEntityByID(message.ReadUInt16()), 0)
    end)
end

if CLIENT then
    Hook.Add("item.equip", "aic_nanocluster_shield_hide_on_equip", function(item)
        if item == nil or item.Removed or string.lower(tostring(item.Prefab.Identifier.Value)) ~= ARMOR_IDENTIFIER then
            return
        end

        for _, delay in ipairs({ 25, 100, 250, 500 }) do
            Timer.Wait(function()
                if item.Removed then
                    return
                end

                local owner = item.ParentInventory and item.ParentInventory.Owner
                HideShieldIfIdle(owner)
            end, delay)
        end
    end)

    Hook.Add("aic_nanocluster_shield_visual_maintain", "aic_nanocluster_shield_visual_maintain", function(effect, deltaTime, item)
        if item == nil or item.Removed then
            return
        end

        local owner = item.ParentInventory and item.ParentInventory.Owner
        HideShieldIfIdle(owner)
    end)
end
