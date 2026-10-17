local CARRIER_IDENTIFIER = "aic_presight_nano_round"
local MISSILE_IDENTIFIER = "aic_presight_nano_swarm_projectile"
local ROCKET_AMMO_IDENTIFIER = "aic_gdf_cluster_rocket_ammo"
local ROCKET_IDENTIFIER = "aic_gdf_cluster_micro_missile"
local GRENADE_IDENTIFIER = "aic_slow_homing_grenade"
local DECOY_IDENTIFIER = "aic_homing_decoy"
local INFECTION_IDENTIFIER = "aic_nanite_infection"
local VOLLEY_SIZE = 8
local DETECTION_RANGE = 1500
local GUIDANCE_DELAY = 0.1
local ROCKET_GUIDANCE_DELAY = 0.2
local TARGET_RECHECK_INTERVAL = 0.2
local TURN_RATE = 3.5 -- radians per second, independent of projectile mass
local ACCELERATION = 10
local MAX_SPEED = 10
local ROTATION_SPEED = 50
local VOLLEY_SPREAD_DEGREES = 30
local BURST_INTERVAL_MS = 100
local BACKPACK_REAR_OFFSET = 25 -- display units behind the torso

local burstProfiles = {
    [CARRIER_IDENTIFIER] = { missile = MISSILE_IDENTIFIER, count = VOLLEY_SIZE, delay = 0 },
    [ROCKET_AMMO_IDENTIFIER] = { missile = ROCKET_IDENTIFIER, count = 5, delay = 100 },
    aic_decoy_carrier = { missile = DECOY_IDENTIFIER, count = 5, delay = 0, interval = 150, spread = 120 },
    aic_decoy_launcher = { missile = DECOY_IDENTIFIER, count = 5, delay = 0, interval = 150, spread = 120 }
}

local emittedCarriers = setmetatable({}, { __mode = "k" })
local missileStates = setmetatable({}, { __mode = "k" })
local candidateCaches = setmetatable({}, { __mode = "k" })
local decoys = setmetatable({}, { __mode = "k" })
local roundGeneration = 0
local backpackCooldowns = setmetatable({}, { __mode = "k" })

local function AddVector(left, right)
    return Vector2(left.X + right.X, left.Y + right.Y)
end

local function SubtractVector(left, right)
    return Vector2(left.X - right.X, left.Y - right.Y)
end

local function LengthSquared(vector)
    return vector.X * vector.X + vector.Y * vector.Y
end

local function RotateVector(vector, angle)
    local cosine = math.cos(angle)
    local sine = math.sin(angle)
    return Vector2(
        vector.X * cosine - vector.Y * sine,
        vector.X * sine + vector.Y * cosine
    )
end

local function GetCurrentAimAngle(owner, launcher, fallbackAngle)
    if owner == nil or owner.Removed then return fallbackAngle end

    local cursorPosition = owner.CursorWorldPosition
    local origin = owner.WorldPosition
    if launcher ~= nil and not launcher.Removed then
        origin = launcher.WorldPosition
    end
    if cursorPosition == nil or origin == nil then return fallbackAngle end

    local direction = SubtractVector(cursorPosition, origin)
    if LengthSquared(direction) <= 1 then return fallbackAngle end
    return math.atan2(direction.Y, direction.X)
end

local function GetIdentifier(item)
    if item == nil or item.Prefab == nil then return "" end
    return string.lower(tostring(item.Prefab.Identifier.Value))
end

local function AsCharacter(value)
    if value ~= nil and LuaUserData.IsTargetType(value, "Barotrauma.Character") then
        return value
    end
    return nil
end

local function ResolveOwner(projectile)
    if projectile == nil then return nil end

    local owner = AsCharacter(projectile.User) or AsCharacter(projectile.Attacker)
    if owner ~= nil then return owner end

    local launcher = projectile.Launcher
    if launcher ~= nil and launcher.ParentInventory ~= nil then
        return AsCharacter(launcher.ParentInventory.Owner)
    end
    return nil
end

local function IsHostile(owner, ownerTeam, target)
    if target == nil or target == owner or target.Removed or target.IsDead or not target.Enabled then
        return false
    end

    if owner == nil and (ownerTeam == nil or ownerTeam == CharacterTeamType.None) then
        return false
    end

    if owner ~= nil then
        local ok, friendly = pcall(function() return owner.IsOnFriendlyTeam(target) end)
        if ok and friendly then return false end
    end

    if ownerTeam ~= nil and ownerTeam ~= CharacterTeamType.None and target.TeamID == ownerTeam then
        return false
    end
    return true
end

local function GetAimLimb(character)
    if character == nil or character.AnimController == nil then return nil end

    local mainLimb = character.AnimController.MainLimb
    if mainLimb ~= nil and not mainLimb.Removed and not mainLimb.IsSevered
        and mainLimb.body ~= nil and mainLimb.body.Enabled then
        return mainLimb
    end

    for limb in character.AnimController.Limbs do
        if not limb.Removed and not limb.IsSevered and limb.body ~= nil and limb.body.Enabled then
            return limb
        end
    end
    return nil
end

local function HasClearPath(item, targetLimb)
    local sourceWorld = item.WorldPosition
    local sourceSim = item.SimPosition
    local sourceSubmarine = item.Submarine
    local targetWorld = targetLimb.WorldPosition
    local targetSubmarine = targetLimb.Submarine
    local difference = ConvertUnits.ToSimUnits(SubtractVector(targetWorld, sourceWorld))

    if targetSubmarine == sourceSubmarine or targetSubmarine == nil then
        return Submarine.CheckVisibility(
            sourceSim,
            AddVector(sourceSim, difference),
            false,
            false,
            true,
            true,
            true
        ) == nil
    end

    if sourceSubmarine == nil then
        return Submarine.CheckVisibility(
            targetLimb.SimPosition,
            SubtractVector(targetLimb.SimPosition, difference),
            false,
            false,
            true,
            true,
            true
        ) == nil
    end

    return Submarine.CheckVisibility(
        sourceSim,
        AddVector(sourceSim, difference),
        false,
        false,
        true,
        true,
        true
    ) == nil and Submarine.CheckVisibility(
        targetLimb.SimPosition,
        SubtractVector(targetLimb.SimPosition, difference),
        false,
        false,
        true,
        true,
        true
    ) == nil
end

local function HasNaniteInfection(character)
    local health = character.CharacterHealth
    -- Infection is limb-specific; include every limb, not just whole-body afflictions.
    return health ~= nil and health.GetAfflictionStrengthByIdentifier(INFECTION_IDENTIFIER, true) > 0
end

local function ValidDecoy(item, state, decoy, data)
    if data == nil or decoy.Removed or decoy.Condition <= 0 or decoy.ParentInventory ~= nil
        or Timer.GetTime() >= data.expires then return false end
    return LengthSquared(SubtractVector(decoy.WorldPosition, item.WorldPosition)) < DETECTION_RANGE ^ 2
end

local function FindDecoy(item, state)
    local best, distance = nil, DETECTION_RANGE ^ 2
    for decoy, data in pairs(decoys) do
        if decoy.Removed or decoy.Condition <= 0 then
            decoys[decoy] = nil
        elseif ValidDecoy(item, state, decoy, data) then
            local d = LengthSquared(SubtractVector(decoy.WorldPosition, item.WorldPosition))
            if d < distance and HasClearPath(item, decoy) then best, distance = decoy, d end
        end
    end
    return best
end

-- Item-level Always runs even after vanilla deactivates slow Projectiles.
Hook.Add("aic_decoy_update", "AIC.NanoSwarm.Decoy", function(effect, deltaTime, item)
    if item == nil or item.Removed or item.Condition <= 0 or item.ParentInventory ~= nil then return end
    local data = decoys[item]
    if data == nil then
        -- A decoy attracts every team, including ownerless guided projectiles.
        data = { expires = Timer.GetTime() + 15 }
        decoys[item] = data
    end
    if Timer.GetTime() >= data.expires then
        if not data.removalQueued and not (CLIENT and Game.IsMultiplayer) then
            data.removalQueued = true
            Entity.Spawner.AddItemToRemoveQueue(item)
        end
        return
    end
    local body = item.body
    if body == nil or not body.Enabled then return end
    local velocity = body.LinearVelocity
    local speed = math.sqrt(LengthSquared(velocity))
    if speed > 0 then
        local factor = math.max(0, speed - 2.5 * (tonumber(deltaTime) or 1 / 60)) / speed
        body.LinearVelocity = Vector2(velocity.X * factor, velocity.Y * factor)
    end
end)

local function GetTargetCandidates(state)
    if state.owner == nil and (state.ownerTeam == nil or state.ownerTeam == CharacterTeamType.None) then
        return {}
    end
    local key = state.owner or state
    local now = Timer.GetTime()
    local cache = candidateCaches[key]
    if cache ~= nil and cache.ownerTeam == state.ownerTeam and now < cache.expires then
        return cache.candidates
    end

    local candidates = {}
    for character in Character.CharacterList do
        if IsHostile(state.owner, state.ownerTeam, character) then
            candidates[#candidates + 1] = { character = character, team = character.TeamID }
        end
    end
    candidateCaches[key] = { ownerTeam = state.ownerTeam,
        expires = now + TARGET_RECHECK_INTERVAL, candidates = candidates }
    return candidates
end

local function FindPreferredTarget(item, state)
    local nearestTarget = nil
    local nearestLimb = nil
    local origin = item.WorldPosition
    local rangeSquared = DETECTION_RANGE * DETECTION_RANGE
    local nearestDistanceSquared = rangeSquared
    local bestPriority = 2

    -- Share only candidates and infection queries; each missile keeps its own range and LOS.
    for _, candidate in ipairs(GetTargetCandidates(state)) do
        local character = candidate.character
        if not character.Removed and not character.IsDead and character.Enabled
            and (character.TeamID == candidate.team or IsHostile(state.owner, state.ownerTeam, character)) then
            local position = character.WorldPosition
            local dx, dy = position.X - origin.X, position.Y - origin.Y
            local distanceSquared = dx * dx + dy * dy
            if distanceSquared < rangeSquared then
                if state.preferUninfected and candidate.infected == nil then
                    candidate.infected = HasNaniteInfection(character)
                end
                local priority = state.preferUninfected and candidate.infected and 1 or 0
                if priority < bestPriority or
                    (priority == bestPriority and distanceSquared < nearestDistanceSquared) then
                    local targetLimb = GetAimLimb(character)
                    if targetLimb ~= nil and IsHostile(state.owner, state.ownerTeam, character)
                        and HasClearPath(item, targetLimb) then
                        nearestTarget = character
                        nearestLimb = targetLimb
                        nearestDistanceSquared = distanceSquared
                        bestPriority = priority
                    end
                end
            end
        end
    end

    return nearestTarget, nearestLimb
end

local function CreateMissileState(item, projectile, owner, launcher, attacker)
    owner = owner or ResolveOwner(projectile)
    return {
        projectile = projectile,
        owner = owner,
        ownerTeam = owner ~= nil and owner.TeamID or CharacterTeamType.None,
        launcher = launcher or (projectile ~= nil and projectile.Launcher or nil),
        attacker = attacker or (projectile ~= nil and projectile.Attacker or nil),
        preferUninfected = GetIdentifier(item) == MISSILE_IDENTIFIER,
        target = nil,
        age = 0,
        targetTimer = 0
    }
end

local function QueueMissile(prefab, spawnPosition, submarine, angle, owner, launcher, attacker, ignoredBodies, damageMultiplier, muzzleOffset)
    local generation = roundGeneration
    local function OnSpawned(spawnedItem)
        if spawnedItem == nil or spawnedItem.Removed then return end
        if generation ~= roundGeneration then
            Entity.Spawner.AddItemToRemoveQueue(spawnedItem)
            return
        end

        local projectile = spawnedItem.GetComponentString("Projectile")
        if projectile == nil then
            Entity.Spawner.AddItemToRemoveQueue(spawnedItem)
            return
        end

        projectile.Launcher = launcher
        projectile.Attacker = attacker
        projectile.IgnoredBodies = ignoredBodies
        missileStates[spawnedItem] = CreateMissileState(
            spawnedItem,
            projectile,
            owner,
            launcher,
            attacker
        )

        local muzzle = spawnedItem.SimPosition
        if muzzleOffset ~= nil then muzzle = AddVector(muzzle, ConvertUnits.ToSimUnits(muzzleOffset)) end
        projectile.Shoot(
            owner,
            spawnedItem.SimPosition,
            muzzle,
            angle,
            ignoredBodies,
            true,
            damageMultiplier,
            0
        )
    end

    if submarine ~= nil then
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, submarine, nil, nil, OnSpawned)
    else
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, nil, nil, OnSpawned)
    end
end

-- Reuse the missile launch/guidance path without a disposable carrier or hand slot.
Hook.Add("aic_cluster_backpack_fire", "AIC.NanoSwarm.Backpack", function(effect, deltaTime, item)
    if CLIENT and Game.IsMultiplayer then return end
    if item == nil or item.Removed or item.Condition <= 0
        or GetIdentifier(item) ~= "aic_cluster_missile_backpack" then return end
    local owner = item.ParentInventory ~= nil and AsCharacter(item.ParentInventory.Owner) or nil
    local function IsWorn()
        return owner ~= nil and not owner.Removed and not owner.IsDead and not owner.IsIncapacitated
            and not item.Removed and item.Condition > 0
            and owner.GetEquippedItem("aic_cluster_missile_backpack", InvSlotType.Bag) == item
    end
    if not IsWorn() then return end
    local now = Timer.GetTime()
    if now < (backpackCooldowns[item] or 0) then return end
    local container = item.GetComponentString("ItemContainer")
    local ammo
    if container ~= nil then
        -- Upward-opening backpack UI puts indices 0-3 on the bottom row.
        -- Ordinary storage is never auto-loaded.
        for slot = 0, 3 do
            local candidate = container.Inventory.GetItemAt(slot)
            local id = GetIdentifier(candidate)
            if candidate ~= nil and not candidate.Removed and candidate.Condition > 0
                and (id == ROCKET_AMMO_IDENTIFIER or id == "aic_decoy_launcher") then
                ammo = candidate
                break
            end
        end
    end
    local ammoId = GetIdentifier(ammo)
    local profile = burstProfiles[ammoId]
    local prefab = profile ~= nil and ItemPrefab.GetItemPrefab(profile.missile) or nil
    if ammo == nil or ammo.Removed or ammo.Condition <= 0
        or (ammoId ~= ROCKET_AMMO_IDENTIFIER and ammoId ~= "aic_decoy_launcher") or prefab == nil then return end
    local torso = owner.AnimController.GetLimb(LimbType.Torso)
    if torso == nil or torso.Removed then return end

    backpackCooldowns[item] = now + 1
    ammo.Condition = 0
    Entity.Spawner.AddItemToRemoveQueue(ammo)
    local generation = roundGeneration
    for index = 1, profile.count do
        Timer.Wait(function()
            if generation ~= roundGeneration or not IsWorn() then return end
            local currentTorso = owner.AnimController.GetLimb(LimbType.Torso)
            if currentTorso == nil or currentTorso.Removed or currentTorso.IsSevered then return end
            local origin = currentTorso.WorldPosition
            -- Physics coordinates are Y-up; backpack missiles launch vertically, not toward the cursor.
            local angle = math.pi / 2
            if profile.spread ~= nil then angle = angle + math.rad((math.random() - 0.5) * profile.spread) end
            local ignoredBodies = {}
            for limb in owner.AnimController.Limbs do
                if limb.body ~= nil and limb.body.FarseerBody ~= nil then
                    ignoredBodies[#ignoredBodies + 1] = limb.body.FarseerBody
                end
            end
            -- Offset toward the backpack, following facing and torso rotation.
            -- Shoot checks the torso-to-muzzle segment so the offset cannot bypass a wall.
            local submarine = currentTorso.Submarine
            local position = submarine ~= nil and ConvertUnits.ToDisplayUnits(currentTorso.SimPosition) or origin
            local muzzleOffset = RotateVector(Vector2(-BACKPACK_REAR_OFFSET * owner.AnimController.Dir, 0),
                currentTorso.body.Rotation)
            QueueMissile(prefab, position, submarine,
                angle,
                owner, item, owner, ignoredBodies, 1, muzzleOffset)
        end, profile.delay + (index - 1) * (profile.interval or BURST_INTERVAL_MS))
    end
end)

local function EmitBurst(item)
    local profile = burstProfiles[GetIdentifier(item)]
    if profile == nil then return end
    local sourceProjectile = item.GetComponentString("Projectile")
    local prefab = ItemPrefab.GetItemPrefab(profile.missile)
    if sourceProjectile == nil or prefab == nil then
        print("[AIC Nano Swarm] Missing carrier component or missile prefab")
        Entity.Spawner.AddItemToRemoveQueue(item)
        return
    end

    local owner = ResolveOwner(sourceProjectile)
    local launcher = sourceProjectile.Launcher
    local attacker = sourceProjectile.Attacker or owner
    local ignoredBodies = sourceProjectile.IgnoredBodies
    local damageMultiplier = 1
    if profile.missile == ROCKET_IDENTIFIER and sourceProjectile.Attack ~= nil then
        damageMultiplier = sourceProjectile.Attack.DamageMultiplier
    end
    local velocity = item.body ~= nil and item.body.LinearVelocity or Vector2(0, 0)
    local baseAngle
    if LengthSquared(velocity) > 0.01 then
        baseAngle = math.atan2(velocity.Y, velocity.X)
    elseif item.body ~= nil then
        baseAngle = item.body.TransformedRotation
    else
        baseAngle = 0
    end
    local submarine = item.Submarine
    -- Spawn coordinates are submarine-local whenever a submarine is supplied.
    local sourcePosition = submarine ~= nil and item.Position or item.WorldPosition
    local fallbackSpawnPosition = Vector2(sourcePosition.X, sourcePosition.Y)
    local launcherOffset = nil
    if launcher ~= nil and not launcher.Removed and launcher.Submarine == submarine then
        local launcherPosition = submarine ~= nil and launcher.Position or launcher.WorldPosition
        launcherOffset = SubtractVector(sourcePosition, launcherPosition)
    end
    local burstGeneration = roundGeneration

    local function FireBurstShot()
        if burstGeneration ~= roundGeneration then return end

        local aimAngle = GetCurrentAimAngle(owner, launcher, baseAngle)
        local spawnPosition = fallbackSpawnPosition
        if launcherOffset ~= nil and launcher ~= nil and not launcher.Removed
            and launcher.Submarine == submarine then
            local launcherPosition = submarine ~= nil and launcher.Position or launcher.WorldPosition
            local currentLauncherOffset = RotateVector(launcherOffset, aimAngle - baseAngle)
            spawnPosition = AddVector(launcherPosition, currentLauncherOffset)
        end

        local randomOffset = (math.random() - 0.5) * (profile.spread or VOLLEY_SPREAD_DEGREES)
        local angle = aimAngle + math.rad(randomOffset)
        QueueMissile(
            prefab,
            spawnPosition,
            submarine,
            angle,
            owner,
            launcher,
            attacker,
            ignoredBodies,
            damageMultiplier
        )
    end

    for index = 1, profile.count do
        local delay = profile.delay + (index - 1) * (profile.interval or BURST_INTERVAL_MS)
        if delay == 0 then
            FireBurstShot()
        else
            Timer.Wait(FireBurstShot, delay)
        end
    end

    Entity.Spawner.AddItemToRemoveQueue(item)
end

local function GetValidTargetLimb(item, state)
    local target = state.target
    if not IsHostile(state.owner, state.ownerTeam, target) then return nil end

    local offset = SubtractVector(target.WorldPosition, item.WorldPosition)
    if LengthSquared(offset) > DETECTION_RANGE * DETECTION_RANGE then return nil end
    return GetAimLimb(target)
end

local function GuideMissile(item, state, deltaTime)
    local body = item.body
    if body == nil or not body.Enabled then return end

    deltaTime = tonumber(deltaTime) or (1 / 60)
    state.age = state.age + deltaTime
    state.targetTimer = state.targetTimer - deltaTime

    local targetLimb = GetValidTargetLimb(item, state)
    if targetLimb == nil then
        state.target = nil
    end
    if state.targetTimer <= 0 then
        state.decoy = FindDecoy(item, state)
        if state.decoy == nil and (state.target == nil or (state.preferUninfected and HasNaniteInfection(state.target))) then
            state.target, targetLimb = FindPreferredTarget(item, state)
        end
        state.targetTimer = TARGET_RECHECK_INTERVAL
    end
    if state.decoy ~= nil then
        if ValidDecoy(item, state, state.decoy, decoys[state.decoy]) then
            targetLimb = state.decoy
        else
            state.decoy = nil
            state.targetTimer = 0
        end
    end

    local velocity = body.LinearVelocity
    local speed = math.sqrt(LengthSquared(velocity))
    local dt = math.max(0, math.min(deltaTime, 0.1))
    if dt == 0 then return end
    local heading = speed > 0.1 and math.atan2(velocity.Y, velocity.X)
        or state.heading or body.TransformedRotation
    local identifier = GetIdentifier(item)
    local maxSpeed = identifier == GRENADE_IDENTIFIER and 5 or MAX_SPEED
    local acceleration = identifier == GRENADE_IDENTIFIER and 12 or ACCELERATION
    local guidanceDelay = identifier == ROCKET_IDENTIFIER and ROCKET_GUIDANCE_DELAY or GUIDANCE_DELAY
    local desiredSpeed = maxSpeed
    if state.age >= guidanceDelay and targetLimb ~= nil then
        local toTarget = SubtractVector(targetLimb.WorldPosition, item.WorldPosition)
        if LengthSquared(toTarget) > 0.001 then
            local targetAngle = math.atan2(toTarget.Y, toTarget.X)
            local difference = math.atan2(math.sin(targetAngle - heading), math.cos(targetAngle - heading))
            -- Retain a turn side at the exactly-behind singularity instead of alternating.
            if math.abs(difference) > math.pi - 0.15 then
                difference = math.abs(difference) * (state.turnSign or 1)
            elseif math.abs(difference) > 0.05 then
                state.turnSign = difference > 0 and 1 or -1
            end
            heading = heading + math.max(-TURN_RATE * dt, math.min(TURN_RATE * dt, difference))
            if math.abs(difference) > math.pi / 2 then desiredSpeed = math.min(desiredSpeed, maxSpeed * 0.35) end
        end
    end
    -- Rotate velocity without cancelling it. Mass-scaled impulse also restarts stalled rounds.
    local nextSpeed = math.min(maxSpeed, speed + math.max(-acceleration * dt,
        math.min(acceleration * dt, desiredSpeed - speed)))
    local nextVelocity = Vector2(math.cos(heading) * nextSpeed, math.sin(heading) * nextSpeed)
    body.ApplyLinearImpulse(Vector2((nextVelocity.X - velocity.X) * body.Mass,
        (nextVelocity.Y - velocity.Y) * body.Mass))
    state.heading = heading
    if identifier == GRENADE_IDENTIFIER then
        -- Stabilize only the shell's attitude: torque on a small body can overshoot
        -- while its flight path is already correct. Preserve collision/translation.
        body.FarseerBody.FixedRotation = true
        body.AngularVelocity = 0
        local rotation = heading + (body.Dir < 0 and math.pi or 0)
        body.SetTransform(body.SimPosition, rotation, false)
    else
        body.SmoothRotate(heading, ROTATION_SPEED, false)
    end
end

Hook.Add("aic_nano_swarm_emit", "AIC.NanoSwarm.Emit", function(effect, deltaTime, item)
    if item == nil or item.Removed or burstProfiles[GetIdentifier(item)] == nil then return end
    -- Only the authority creates entities; clients still guide replicated missiles for smooth visuals.
    if CLIENT and Game.IsMultiplayer then return end
    if emittedCarriers[item] then return end

    emittedCarriers[item] = true
    EmitBurst(item)
end)

Hook.Add("aic_nano_swarm_guide", "AIC.NanoSwarm.Guide", function(effect, deltaTime, item)
    if item == nil or item.Removed then return end
    if item.Condition <= 0 then
        missileStates[item] = nil
        return
    end
    local state = missileStates[item]
    if state == nil then
        local identifier = GetIdentifier(item)
        if identifier ~= MISSILE_IDENTIFIER and identifier ~= ROCKET_IDENTIFIER and identifier ~= GRENADE_IDENTIFIER then return end
        local projectile = item.GetComponentString("Projectile")
        if projectile == nil then return end
        state = CreateMissileState(item, projectile)
        missileStates[item] = state
    elseif state.owner == nil then
        state.owner = ResolveOwner(state.projectile)
    end
    if state.owner ~= nil and state.ownerTeam ~= state.owner.TeamID then
        state.ownerTeam = state.owner.TeamID
        state.target = nil
        state.targetTimer = 0
    end

    GuideMissile(item, state, deltaTime)
end)

Hook.Add("roundEnd", "AIC.NanoSwarm.Reset", function()
    roundGeneration = roundGeneration + 1
    backpackCooldowns = setmetatable({}, { __mode = "k" })
    emittedCarriers = setmetatable({}, { __mode = "k" })
    missileStates = setmetatable({}, { __mode = "k" })
    candidateCaches = setmetatable({}, { __mode = "k" })
    decoys = setmetatable({}, { __mode = "k" })
end)
