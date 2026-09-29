if CLIENT and Game.IsMultiplayer then return end

local PRIMARY_HOOK = "aic_oracle_arc_primary_impact"
local TIER_ONE_HOOK = "aic_oracle_arc_tier1_impact"
local TIER_ONE_PROJECTILE = "aic_practitioner_oracle_arc_jump_1"
local TIER_TWO_PROJECTILE = "aic_practitioner_oracle_arc_jump_2"
local BRANCH_COUNT = 2

local function AddVector(left, right)
    return Vector2(left.X + right.X, left.Y + right.Y)
end

local function SubtractVector(left, right)
    return Vector2(left.X - right.X, left.Y - right.Y)
end

local function GetImpactCharacter(targets)
    if targets == nil then return nil end

    local target = targets[1]
    if target == nil then return nil end
    if LuaUserData.IsTargetType(target, "Barotrauma.Character") then
        return target
    end
    if LuaUserData.IsTargetType(target, "Barotrauma.Limb") then
        return target.character
    end
    return nil
end

local function GetSourcePoint(character, worldPosition)
    local sourceWorld = worldPosition or character.WorldPosition
    sourceWorld = Vector2(sourceWorld.X, sourceWorld.Y)

    local offset = ConvertUnits.ToSimUnits(SubtractVector(sourceWorld, character.WorldPosition))
    local sourceSim = AddVector(character.SimPosition, offset)
    return sourceWorld, sourceSim
end

local function HasClearPath(sourceWorld, sourceSim, sourceSubmarine, targetLimb)
    local targetWorld = targetLimb.WorldPosition
    local difference = ConvertUnits.ToSimUnits(SubtractVector(targetWorld, sourceWorld))
    local targetSubmarine = targetLimb.Submarine

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

local function FindAimLimb(character, sourceWorld, sourceSim, sourceSubmarine, rangeSquared)
    local limbsInRange = {}

    for limb in character.AnimController.Limbs do
        local body = limb.body
        if not limb.Removed and not limb.IsSevered and body ~= nil and body.Enabled then
            local offset = SubtractVector(limb.WorldPosition, sourceWorld)
            local distanceSquared = offset.X * offset.X + offset.Y * offset.Y
            if distanceSquared <= rangeSquared then
                limbsInRange[#limbsInRange + 1] = {
                    limb = limb,
                    distanceSquared = distanceSquared
                }
            end
        end
    end

    table.sort(limbsInRange, function(left, right)
        return left.distanceSquared < right.distanceSquared
    end)

    -- Test the nearest limbs first to avoid raycasting every segment of large creatures.
    for _, entry in ipairs(limbsInRange) do
        if HasClearPath(sourceWorld, sourceSim, sourceSubmarine, entry.limb) then
            return entry.limb, entry.distanceSquared
        end
    end

    return nil, rangeSquared
end

local function FindTargets(sourceCharacter, sourceWorld, sourceSim, jumpRange)
    local candidates = {}
    local rangeSquared = jumpRange * jumpRange
    local sourceSubmarine = sourceCharacter.Submarine

    for character in Character.CharacterList do
        if character ~= sourceCharacter
            and not character.Removed
            and character.Enabled
            and not character.IsDead
            and character.AnimController ~= nil then
            local aimLimb, distanceSquared = FindAimLimb(
                character,
                sourceWorld,
                sourceSim,
                sourceSubmarine,
                rangeSquared
            )
            if aimLimb ~= nil then
                candidates[#candidates + 1] = {
                    character = character,
                    limb = aimLimb,
                    distanceSquared = distanceSquared
                }
            end
        end
    end

    table.sort(candidates, function(left, right)
        if left.distanceSquared == right.distanceSquared then
            return tostring(left.character.ID) < tostring(right.character.ID)
        end
        return left.distanceSquared < right.distanceSquared
    end)

    local selected = {}
    for index = 1, math.min(BRANCH_COUNT, #candidates) do
        selected[index] = candidates[index]
    end
    return selected
end

local function GetIgnoredBodies(character)
    -- Only ignore this hop's origin. Earlier targets remain eligible, allowing A -> B -> A.
    local ignoredBodies = {}
    for limb in character.AnimController.Limbs do
        if limb.body ~= nil and limb.body.FarseerBody ~= nil then
            ignoredBodies[#ignoredBodies + 1] = limb.body.FarseerBody
        end
    end
    return ignoredBodies
end

local function SpawnArc(projectileIdentifier, sourceCharacter, sourceSim, target, user, launcher, attacker, damageMultiplier)
    local prefab = ItemPrefab.GetItemPrefab(projectileIdentifier)
    if prefab == nil then
        print("[AIC] Missing oracle arc projectile prefab: " .. projectileIdentifier)
        return
    end

    local sourceSubmarine = sourceCharacter.Submarine
    local spawnPosition = ConvertUnits.ToDisplayUnits(sourceSim)
    local ignoredBodies = GetIgnoredBodies(sourceCharacter)

    local function OnSpawned(spawnedItem)
        if spawnedItem == nil or spawnedItem.Removed
            or target.character == nil or target.character.Removed or target.character.IsDead
            or target.limb == nil or target.limb.Removed or target.limb.IsSevered then
            if spawnedItem ~= nil and not spawnedItem.Removed then
                Entity.Spawner.AddItemToRemoveQueue(spawnedItem)
            end
            return
        end

        local projectile = spawnedItem.GetComponentString("Projectile")
        if projectile == nil then
            Entity.Spawner.AddItemToRemoveQueue(spawnedItem)
            return
        end

        local direction = SubtractVector(target.limb.WorldPosition, spawnedItem.WorldPosition)
        if direction.X * direction.X + direction.Y * direction.Y < 0.0001 then
            Entity.Spawner.AddItemToRemoveQueue(spawnedItem)
            return
        end

        projectile.IgnoredBodies = ignoredBodies
        projectile.Launcher = launcher
        projectile.Attacker = attacker
        projectile.Shoot(
            user,
            spawnedItem.SimPosition,
            spawnedItem.SimPosition,
            math.atan2(direction.Y, direction.X),
            projectile.IgnoredBodies,
            true,
            damageMultiplier,
            0
        )
    end

    if sourceSubmarine ~= nil then
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, sourceSubmarine, nil, nil, OnSpawned)
    else
        Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPosition, nil, nil, OnSpawned)
    end
end

local function EmitBranches(item, targets, worldPosition, projectileIdentifier, jumpRange)
    if item == nil or item.Removed then return end

    local sourceCharacter = GetImpactCharacter(targets)
    if sourceCharacter == nil or sourceCharacter.Removed or sourceCharacter.AnimController == nil then return end

    local sourceWorld, sourceSim = GetSourcePoint(sourceCharacter, worldPosition)
    local selectedTargets = FindTargets(sourceCharacter, sourceWorld, sourceSim, jumpRange)
    if #selectedTargets == 0 then return end

    local sourceProjectile = item.GetComponentString("Projectile")
    local user = sourceProjectile ~= nil and sourceProjectile.User or nil
    local launcher = sourceProjectile ~= nil and sourceProjectile.Launcher or nil
    local attacker = sourceProjectile ~= nil and (sourceProjectile.Attacker or sourceProjectile.User) or nil
    -- Carry the shot's multiplier forward unchanged; tier prefabs already contain damage falloff.
    local damageMultiplier = sourceProjectile ~= nil and sourceProjectile.Attack ~= nil
        and sourceProjectile.Attack.DamageMultiplier or 1

    for _, target in ipairs(selectedTargets) do
        SpawnArc(projectileIdentifier, sourceCharacter, sourceSim, target, user, launcher, attacker, damageMultiplier)
    end
end

Hook.Add(PRIMARY_HOOK, "AIC.OracleArcChain.Primary", function(effect, deltaTime, item, targets, worldPosition)
    EmitBranches(item, targets, worldPosition, TIER_ONE_PROJECTILE, 400)
end)

Hook.Add(TIER_ONE_HOOK, "AIC.OracleArcChain.TierOne", function(effect, deltaTime, item, targets, worldPosition)
    EmitBranches(item, targets, worldPosition, TIER_TWO_PROJECTILE, 300)
end)
