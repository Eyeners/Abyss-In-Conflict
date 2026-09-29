if not CLIENT then return end

local AFFLICTION_IDENTIFIER = "aic_heavy_load"
local AIM_DROP_SPEED = math.rad(18)
local MAX_AIM_DROP = math.rad(85)
local MAX_FRAME_TIME = 0.05

local lastUpdateTime = Timer.GetTime()
local accumulatedDrop = 0
local lastCharacter = nil

local function HasHeavyLoad(character)
    if character == nil or character.CharacterHealth == nil then return false end
    return (character.CharacterHealth.GetAfflictionStrengthByIdentifier(AFFLICTION_IDENTIFIER, true) or 0) > 0.1
end

local function IsAimingRangedWeapon(character)
    if character == nil or character.IsKeyDown == nil or not character.IsKeyDown(InputType.Aim) then
        return false
    end

    if character.Inventory == nil then return false end
    local right = character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand)
    local left = character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand)
    return (right ~= nil and right.GetComponentString("RangedWeapon") ~= nil)
        or (left ~= nil and left.GetComponentString("RangedWeapon") ~= nil)
end

local function ResetDrop(now)
    accumulatedDrop = 0
    lastUpdateTime = now
end

Hook.Patch("Barotrauma.Character", "ControlLocalPlayer", function(instance, ptable)
    -- Other characters must not reset the controlled character's accumulated drop.
    if instance == nil or instance ~= Character.Controlled then return end
    local now = Timer.GetTime()
    local deltaTime = math.min(math.max(now - lastUpdateTime, 0), MAX_FRAME_TIME)
    lastUpdateTime = now

    local character = instance
    if character ~= lastCharacter then
        lastCharacter = character
        ResetDrop(now)
    end
    if not HasHeavyLoad(character)
        or not IsAimingRangedWeapon(character) then
        ResetDrop(now)
        return
    end
    if deltaTime <= 0 then return end

    local screen = Screen.Selected
    if screen == nil or screen.Cam == nil or PlayerInput.MousePosition == nil then return end

    accumulatedDrop = math.min(accumulatedDrop + AIM_DROP_SPEED * deltaTime, MAX_AIM_DROP)
    local rawWorldCursor = screen.Cam.ScreenToWorld(PlayerInput.MousePosition)
    local origin = character.WorldPosition
    local dx = rawWorldCursor.X - origin.X
    local dy = rawWorldCursor.Y - origin.Y
    local distance = math.max(math.sqrt(dx * dx + dy * dy), 100)
    -- Elevation relative to the horizontal keeps left-facing and right-facing aim symmetric.
    local elevation = math.atan2(dy, math.abs(dx))
    local lowered = math.max(-math.pi / 2 + 0.01, elevation - accumulatedDrop)
    local direction = dx < 0 and -1 or 1
    local droppedWorldCursor = Vector2(
        origin.X + direction * math.cos(lowered) * distance,
        origin.Y + math.sin(lowered) * distance
    )

    if character.Submarine ~= nil then
        droppedWorldCursor = Vector2(
            droppedWorldCursor.X - character.Submarine.Position.X,
            droppedWorldCursor.Y - character.Submarine.Position.Y
        )
    end
    character.CursorPosition = droppedWorldCursor
end, Hook.HookMethodType.After)
