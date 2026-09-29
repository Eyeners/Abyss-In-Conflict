local ARMOR = "aic_test_shoulder_cannon_armor"
local PROJECTILE = "aic_gdf_redstar_plasma_beam"
local RELOAD = 0.3
-- Display-unit offset for this armor's fixed 1.2x rig, not another scale operation.
local SHOULDER_FORWARD = 12
local SHOULDER_UP = 30
local states = setmetatable({}, { __mode = "k" })
local generation = 0

Hook.Add("item.equip", "AIC.ShoulderCannon.Equip", function(item)
    states[item] = nil
end)

Hook.Add("aic_shoulder_cannon_update", "AIC.ShoulderCannon.Update", function(effect, deltaTime, item)
    if CLIENT and Game.IsMultiplayer then return end
    if item == nil or item.Removed or item.Condition <= 0 then return end
    local owner = item.ParentInventory ~= nil and item.ParentInventory.Owner or nil
    if owner == nil or not LuaUserData.IsTargetType(owner, "Barotrauma.Character") then return end
    local function IsWorn()
        return not owner.Removed and not owner.IsDead and not owner.IsIncapacitated
            and not item.Removed and item.Condition > 0
            and owner.GetEquippedItem(ARMOR, InvSlotType.OuterClothes) == item
    end
    if not IsWorn() then states[item] = nil return end
    local anim = owner.AnimController
    local state = states[item]
    -- Collection wrappers are not stable identity tokens across Lua/C# calls.
    -- Equip invalidates the cache; disposed limbs also invalidate it after a rig rebuild.
    if state == nil or state.owner ~= owner or state.torso.Removed then
        local torso = anim.GetLimb(LimbType.Torso)
        if torso == nil or torso.Removed or torso.body == nil then return end
        local ignored = {}
        for limb in anim.Limbs do
            if limb.body ~= nil and limb.body.FarseerBody ~= nil then
                ignored[#ignored + 1] = limb.body.FarseerBody
            end
        end
        state = { owner = owner, torso = torso,
            ignored = ignored, nextShot = state ~= nil and state.nextShot or 0 }
        states[item] = state
        print("[AIC ShoulderCannon] READY: aim + shoot")
    end
    -- Read the replicated controls without consuming or replacing hand-weapon input.
    if not owner.IsKeyDown(InputType.Aim) or not owner.IsKeyDown(InputType.Shoot) then return end
    if not state.inputReported then
        state.inputReported = true
        print("[AIC ShoulderCannon] INPUT received")
    end
    local now = Timer.GetTime()
    if now < state.nextShot or state.torso.Removed or state.torso.IsSevered then return end
    local prefab = ItemPrefab.GetItemPrefab(PROJECTILE)
    if prefab == nil then
        if not state.missingReported then
            state.missingReported = true
            print("[AIC ShoulderCannon] Missing projectile: " .. PROJECTILE)
        end
        return
    end
    state.nextShot = now + RELOAD
    local currentGeneration = generation
    local torso = state.torso
    local submarine = torso.Submarine
    local spawnPosition = submarine ~= nil and ConvertUnits.ToDisplayUnits(torso.SimPosition)
        or torso.WorldPosition
    local function OnSpawned(round)
        if round == nil or round.Removed then return end
        if generation ~= currentGeneration or states[item] ~= state or not IsWorn()
            or torso.Removed or torso.IsSevered
            or torso.Submarine ~= submarine then
            Entity.Spawner.AddItemToRemoveQueue(round)
            return
        end
        local projectile = round.GetComponentString("Projectile")
        if projectile == nil then Entity.Spawner.AddItemToRemoveQueue(round) return end
        local rotation = torso.body.Rotation
        local x = SHOULDER_FORWARD * anim.Dir
        local offset = Vector2(x * math.cos(rotation) - SHOULDER_UP * math.sin(rotation),
            x * math.sin(rotation) + SHOULDER_UP * math.cos(rotation))
        local world = torso.WorldPosition
        local cursor = owner.CursorWorldPosition
        local angle = math.atan2(cursor.Y - world.Y - offset.Y, cursor.X - world.X - offset.X)
        local basePosition = torso.SimPosition
        local localOffset = ConvertUnits.ToSimUnits(offset)
        local muzzle = Vector2(basePosition.X + localOffset.X, basePosition.Y + localOffset.Y)
        projectile.Launcher = item
        projectile.Attacker = owner
        projectile.IgnoredBodies = state.ignored
        -- Native Shoot checks the segment from the torso to the requested muzzle.
        projectile.Shoot(owner, basePosition, muzzle, angle, state.ignored, true, 1, 0)
        if not state.fireReported then
            state.fireReported = true
            print("[AIC ShoulderCannon] FIRED")
        end
    end
    if submarine ~= nil then
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, submarine, nil, nil, OnSpawned)
    else
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, nil, nil, OnSpawned)
    end
end)

Hook.Add("roundEnd", "AIC.ShoulderCannon.Reset", function()
    generation = generation + 1
    states = setmetatable({}, { __mode = "k" })
end)
