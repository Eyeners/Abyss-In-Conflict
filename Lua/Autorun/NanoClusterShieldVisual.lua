AIC = AIC or {}

local ARMOR_IDENTIFIER = "aic_foresight_t3_armor"
local SHIELD_LIMB_NAME = "AIC Nano Cluster Shield"
local FLASH_EVENT = "aic_nanocluster_shield_flash"
local FLASH_DURATION = 0.25
local UPDATE_INTERVAL = 0.05
local LIMB_RETRY_DURATION = 0.5
local NETWORK_INTERVAL = 0.05

local limbCache = setmetatable({}, { __mode = "k" })
local visualStates = setmetatable({}, { __mode = "k" })
local networkFlashes = setmetatable({}, { __mode = "k" })
local pendingEquips = setmetatable({}, { __mode = "k" })
local nextUpdateTime = 0

local function FindShieldLimb(character)
    if not character or character.Removed or not character.AnimController then return nil end
    local controller = character.AnimController
    local cached = limbCache[character]
    if cached and cached.controller == controller and not cached.limb.Removed
        and not cached.limb.IsSevered then
        return cached.limb
    end
    limbCache[character] = nil
    for limb in controller.Limbs do
        if not limb.Removed and not limb.IsSevered and tostring(limb.Name) == SHIELD_LIMB_NAME then
            limbCache[character] = { controller = controller, limb = limb }
            return limb
        end
    end
end

local function QueueVisual(character, expires)
    local now = Timer.GetTime()
    local state = visualStates[character]
    if not state then
        state = { expires = 0, retryUntil = now + LIMB_RETRY_DURATION }
        visualStates[character] = state
    end
    if expires then
        state.expires = expires
        state.retryUntil = math.max(state.retryUntil, expires)
    end
    return state
end

local function HideShieldIfIdle(character)
    if not CLIENT or not character or character.Removed then return end
    local state = visualStates[character]
    if state and state.expires > Timer.GetTime() then return end
    local limb = FindShieldLimb(character)
    if limb then
        limb.Alpha = 0
    else
        QueueVisual(character)
    end
end

local function FlashShieldLocal(character)
    if not CLIENT or not character or character.Removed then return end
    QueueVisual(character, Timer.GetTime() + FLASH_DURATION)
    local limb = FindShieldLimb(character)
    if limb then limb.Alpha = 1 end
end

local function SendShieldFlash(character)
    local message = Networking.Start(FLASH_EVENT)
    message.WriteUInt16(character.ID)
    Networking.Send(message)
end

local function EmitShieldFlash(character)
    if not character or character.Removed then return end
    if Game.IsMultiplayer then
        if not SERVER then return end
        local now = Timer.GetTime()
        local state = networkFlashes[character]
        if not state then
            state = { nextSend = 0, pending = false }
            networkFlashes[character] = state
        end
        if now >= state.nextSend then
            SendShieldFlash(character)
            state.nextSend = now + NETWORK_INTERVAL
            state.pending = false
        else
            -- Coalesce only the visual notification, including the last hit in the window.
            state.pending = true
        end
    else
        FlashShieldLocal(character)
    end
end

AIC.NanoClusterShieldVisual = { Flash = EmitShieldFlash }

if CLIENT and Game.IsMultiplayer then
    Networking.Receive(FLASH_EVENT, function(message)
        FlashShieldLocal(Entity.FindEntityByID(message.ReadUInt16()))
    end)
end

local function EquippedOwner(item)
    if not item or item.Removed or not item.ParentInventory then return nil end
    local owner = item.ParentInventory.Owner
    if owner and LuaUserData.IsTargetType(owner, "Barotrauma.Character") then return owner end
end

-- One updater per side replaces per-hit timers, including retries while the limb is being created.
Hook.Add("think", "AIC.NanoClusterShieldVisual.Update", function()
    if next(visualStates) == nil and next(networkFlashes) == nil and next(pendingEquips) == nil then return end
    local now = Timer.GetTime()
    if now < nextUpdateTime then return end
    nextUpdateTime = now + UPDATE_INTERVAL

    if SERVER then
        for character, state in pairs(networkFlashes) do
            if character.Removed then
                networkFlashes[character] = nil
            elseif now >= state.nextSend then
                if state.pending then
                    SendShieldFlash(character)
                    state.nextSend = now + NETWORK_INTERVAL
                    state.pending = false
                else
                    networkFlashes[character] = nil
                end
            end
        end
    end

    if CLIENT then
        for item, expires in pairs(pendingEquips) do
            local owner = EquippedOwner(item)
            if owner then
                limbCache[owner] = nil
                HideShieldIfIdle(owner)
                pendingEquips[item] = nil
            elseif item.Removed or now >= expires then
                pendingEquips[item] = nil
            end
        end
        for character, state in pairs(visualStates) do
            if character.Removed then
                visualStates[character] = nil
                limbCache[character] = nil
            else
                local limb = FindShieldLimb(character)
                if limb then
                    limb.Alpha = math.max(0, math.min(1, (state.expires - now) / FLASH_DURATION))
                    if now >= state.expires then visualStates[character] = nil end
                elseif now >= state.retryUntil then
                    visualStates[character] = nil
                end
            end
        end
    end
end)

if CLIENT then
    Hook.Add("item.equip", "aic_nanocluster_shield_hide_on_equip", function(item)
        if not item or item.Removed or tostring(item.Prefab.Identifier.Value) ~= ARMOR_IDENTIFIER then return end
        local owner = EquippedOwner(item)
        if owner then
            limbCache[owner] = nil
            HideShieldIfIdle(owner)
        else
            -- Equip may be raised before the item has entered the character inventory.
            pendingEquips[item] = Timer.GetTime() + LIMB_RETRY_DURATION
        end
    end)

    Hook.Add("aic_nanocluster_shield_visual_maintain", "aic_nanocluster_shield_visual_maintain", function(effect, deltaTime, item)
        HideShieldIfIdle(EquippedOwner(item))
    end)
end

Hook.Add("roundEnd", "AIC.NanoClusterShieldVisual.Reset", function()
    if CLIENT then
        for _, cached in pairs(limbCache) do
            if not cached.limb.Removed then cached.limb.Alpha = 0 end
        end
    end
    limbCache = setmetatable({}, { __mode = "k" })
    visualStates = setmetatable({}, { __mode = "k" })
    networkFlashes = setmetatable({}, { __mode = "k" })
    pendingEquips = setmetatable({}, { __mode = "k" })
    nextUpdateTime = 0
end)
