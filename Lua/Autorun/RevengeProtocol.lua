if CLIENT and Game.IsMultiplayer then return end

local PROTOCOL_AFFLICTION = "aic_revenge_protocol"
local PROTOCOL_SPECIES = "human_aic_revenge_protocol"
local TRANSFORM_THRESHOLD = 0.5
local MATCH_DISTANCE_SQUARED = 500 * 500
local PENDING_LIFETIME = 5
local MELEE_PREPARE_DISTANCE_SQUARED = 350 * 350
local MELEE_USE_DISTANCE_SQUARED = 150 * 150
local MELEE_HAND_PHASE = 0.9

local pendingTransformations = {}
local protocolUnits = {}
local meleeStates = {}

local function IsProtocolUnit(character)
    return character ~= nil and
        string.lower(tostring(character.SpeciesName)) == PROTOCOL_SPECIES
end

local function IgnoreAlliedCharacters(aiController)
    if aiController == nil or not IsProtocolUnit(aiController.Character) then
        return
    end

    local character = aiController.Character
    local team = character.TeamID
    if team == nil or team == CharacterTeamType.None then
        return
    end

    for other in Character.CharacterList do
        if other ~= character and
            not other.Removed and
            other.TeamID == team and
            other.AiTarget ~= nil then
            aiController.IgnoreTarget(other.AiTarget)
        end
    end
end

local function ResolveAttackTarget(aiController)
    local selectedTarget = aiController.SelectedAiTarget
    if selectedTarget == nil or selectedTarget.Entity == nil then
        return nil
    end

    local entity = selectedTarget.Entity
    if LuaUserData.IsTargetType(entity, "Barotrauma.Character") then
        return entity
    end
    if LuaUserData.IsTargetType(entity, "Barotrauma.Item") then
        local owner = entity.GetRootInventoryOwner()
        if owner ~= nil and LuaUserData.IsTargetType(owner, "Barotrauma.Character") then
            return owner
        end
    end
    return nil
end

local function DriveProtocolMelee(aiController, deltaTime)
    local character = aiController.Character
    if character == nil then
        return
    end
    local isProtocolUnit = protocolUnits[character]
    if isProtocolUnit == nil then
        isProtocolUnit = IsProtocolUnit(character)
        protocolUnits[character] = isProtocolUnit
    end
    if not isProtocolUnit then
        return
    end
    if character.Removed or character.IsDead or not character.AllowInput then
        return
    end

    local target = ResolveAttackTarget(aiController)
    if target == nil or target.Removed or target.IsDead or target == character then
        return
    end
    if character.TeamID ~= nil and
        character.TeamID ~= CharacterTeamType.None and
        character.TeamID == target.TeamID then
        return
    end

    local dx = target.WorldPosition.X - character.WorldPosition.X
    local dy = target.WorldPosition.Y - character.WorldPosition.Y
    local distanceSquared = dx * dx + dy * dy
    if distanceSquared > MELEE_PREPARE_DISTANCE_SQUARED then
        return
    end

    local cursorPosition = target.WorldPosition
    if character.Submarine ~= nil then
        cursorPosition = cursorPosition - character.Submarine.Position
    end
    character.CursorPosition = cursorPosition
    character.SetInput(InputType.Aim, false, true)

    if distanceSquared > MELEE_USE_DISTANCE_SQUARED or character.Inventory == nil then
        return
    end

    local state = meleeStates[character]
    if state == nil then
        state = { useLeft = false, phaseTimer = 0 }
        meleeStates[character] = state
    end
    state.phaseTimer = state.phaseTimer + (deltaTime or 0)
    if state.phaseTimer >= MELEE_HAND_PHASE then
        state.phaseTimer = state.phaseTimer - MELEE_HAND_PHASE
        state.useLeft = not state.useLeft
    end

    local preferredSlot = state.useLeft and InvSlotType.LeftHand or InvSlotType.RightHand
    local fallbackSlot = state.useLeft and InvSlotType.RightHand or InvSlotType.LeftHand
    local blade = character.Inventory.GetItemInLimbSlot(preferredSlot)
    if blade == nil or tostring(blade.Prefab.Identifier) ~= "aic_revenge_protocol_blade_ai" then
        blade = character.Inventory.GetItemInLimbSlot(fallbackSlot)
    end
    if blade == nil or tostring(blade.Prefab.Identifier) ~= "aic_revenge_protocol_blade_ai" then
        return
    end

    -- Keep swing, collision and damage native; only bypass a stale transformed aim state.
    character.SetInput(InputType.Shoot, false, true)
    local requireAimToUse = blade.RequireAimToUse
    blade.RequireAimToUse = false
    blade.Use(deltaTime, character, nil, nil, character)
    blade.RequireAimToUse = requireAimToUse
end

local function RemoveExpired(now)
    for index = #pendingTransformations, 1, -1 do
        if pendingTransformations[index].expiresAt < now then
            table.remove(pendingTransformations, index)
        end
    end
end

local function GetProtocolStrength(character)
    if character == nil or character.CharacterHealth == nil then
        return 0
    end

    return character.CharacterHealth.GetAfflictionStrengthByIdentifier(PROTOCOL_AFFLICTION, true) or 0
end

Hook.Add("character.death", "AIC.RevengeProtocol.RecordTeam", function(character)
    if character == nil then
        return
    end
    protocolUnits[character] = nil
    meleeStates[character] = nil
    if character.Removed or GetProtocolStrength(character) < TRANSFORM_THRESHOLD then
        return
    end

    local now = Timer.GetTime()
    RemoveExpired(now)

    pendingTransformations[#pendingTransformations + 1] = {
        team = character.TeamID,
        x = character.WorldPosition.X,
        y = character.WorldPosition.Y,
        expiresAt = now + PENDING_LIFETIME
    }
end)

Hook.Add("character.created", "AIC.RevengeProtocol.RestoreTeam", function(character)
    local isProtocolUnit = IsProtocolUnit(character)
    protocolUnits[character] = isProtocolUnit
    if not isProtocolUnit then
        return
    end

    local now = Timer.GetTime()
    RemoveExpired(now)

    local closestIndex = nil
    local closestDistanceSquared = MATCH_DISTANCE_SQUARED
    for index, entry in ipairs(pendingTransformations) do
        local dx = character.WorldPosition.X - entry.x
        local dy = character.WorldPosition.Y - entry.y
        local distanceSquared = dx * dx + dy * dy
        if distanceSquared <= closestDistanceSquared then
            closestIndex = index
            closestDistanceSquared = distanceSquared
        end
    end

    if closestIndex == nil then
        return
    end

    local entry = table.remove(pendingTransformations, closestIndex)
    -- Native husk conversion resets CharacterInfo.TeamID immediately after creation.
    Timer.Wait(function()
        if character ~= nil and not character.Removed then
            character.TeamID = entry.team
            IgnoreAlliedCharacters(character.AIController)
        end
    end, 1)
end)

-- EnemyAI target definitions do not automatically exclude friendly characters.
-- Refresh the native temporary ignore set only when this unit reevaluates targets.
Hook.Patch(
    "AIC.RevengeProtocol.IgnoreAllies",
    "Barotrauma.EnemyAIController",
    "UpdateTargets",
    function(instance, ptable)
        IgnoreAlliedCharacters(instance)
    end,
    Hook.HookMethodType.Before
)

Hook.Patch(
    "AIC.RevengeProtocol.DriveMelee",
    "Barotrauma.EnemyAIController",
    "Update",
    { "System.Single" },
    function(instance, ptable)
        DriveProtocolMelee(instance, ptable["deltaTime"])
    end,
    Hook.HookMethodType.After
)

Hook.Add("roundEnd", "AIC.RevengeProtocol.ClearPending", function()
    pendingTransformations = {}
    protocolUnits = {}
    meleeStates = {}
end)
