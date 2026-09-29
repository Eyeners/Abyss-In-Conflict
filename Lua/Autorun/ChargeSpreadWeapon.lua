local WEAPON_IDENTIFIER = "aic_test_plasma_shotgun"
local ORACLE_IDENTIFIER = "aic_practitioner_oracle_magnetic_storm_generator"
local ORACLE_MAGAZINE = "aic_practitioner_oracle_arc_magazine"
local ORACLE_PRIMARY = "aic_practitioner_oracle_arc_bolt"
local ORACLE_TIER_ONE = "aic_practitioner_oracle_arc_jump_1"
local ORACLE_TIER_TWO = "aic_practitioner_oracle_arc_jump_2"
local UNCHARGED_SPREAD = 20
local CHARGED_SPREAD = 3
local BASE_DAMAGE_MULTIPLIER = 1
local MAX_DAMAGE_MULTIPLIER = 2.5
local BASE_PENETRATION = 0.1
local MAX_PENETRATION = 0.5
local TAP_RELEASE_CHECK_DELAY = 25
local TAP_RELEASE_DELTA_TIME = 1 / 60
local IDLE_TUNING_INTERVAL = 0.2

local rangedWeaponDescriptor = Descriptors["Barotrauma.Items.Components.RangedWeapon"]
local projectileDescriptor = Descriptors["Barotrauma.Items.Components.Projectile"]

pcall(function()
    LuaUserData.MakeFieldAccessible(rangedWeaponDescriptor, "currentChargeTime")
    LuaUserData.MakeFieldAccessible(rangedWeaponDescriptor, "tryingToCharge")
    LuaUserData.MakePropertyAccessible(rangedWeaponDescriptor, "WeaponDamageModifier")
    LuaUserData.MakePropertyAccessible(rangedWeaponDescriptor, "Penetration")
    LuaUserData.MakePropertyAccessible(projectileDescriptor, "Spread")
end)

local BindingFlags = nil
pcall(function()
    BindingFlags = LuaUserData.CreateEnumTable("System.Reflection.BindingFlags")
end)

local fields = {}
local setterModes = {}
local chargeReadMode = nil
local reportedErrors = {}
local states = setmetatable({}, { __mode = "k" })
local projectileSpreads = setmetatable({}, { __mode = "k" })
local oracleShots = setmetatable({}, { __mode = "k" })
local oracleProjectileShots = setmetatable({}, { __mode = "k" })
local oracleRefills = setmetatable({}, { __mode = "k" })
local roundGeneration = 0

local function ReportOnce(key, message)
    if reportedErrors[key] then return end
    reportedErrors[key] = true
    print("[AIC Charge Weapon] " .. message)
end

local function ResolveField(instance, cacheKey, fieldName)
    if fields[cacheKey] ~= nil then
        return fields[cacheKey] or nil
    end
    if instance == nil or BindingFlags == nil then
        fields[cacheKey] = false
        return nil
    end

    local ok, field = pcall(function()
        local flags = BindingFlags.Instance + BindingFlags.Public + BindingFlags.NonPublic
        return instance.GetType().GetField(fieldName, flags)
    end)
    fields[cacheKey] = ok and field or false
    return fields[cacheKey] or nil
end

local function SetCurrentChargeTime(weapon, value)
    value = Single(value)
    if setterModes.currentChargeTime ~= "field" then
        local ok = pcall(function() weapon.currentChargeTime = value end)
        if ok then
            setterModes.currentChargeTime = "direct"
            return true
        end
        setterModes.currentChargeTime = "field"
    end

    local field = ResolveField(weapon, "currentChargeTime", "currentChargeTime")
    local ok = field ~= nil and pcall(function() field.SetValue(weapon, value) end)
    if not ok then ReportOnce("currentChargeTime", "cannot write currentChargeTime") end
    return ok == true
end

local function GetCurrentChargeTime(weapon)
    if chargeReadMode ~= "field" then
        local ok, value = pcall(function() return weapon.currentChargeTime end)
        if ok then return tonumber(value) or 0 end
        chargeReadMode = "field"
    end

    local field = ResolveField(weapon, "currentChargeTime", "currentChargeTime")
    if field == nil then return 0 end
    local ok, value = pcall(function() return field.GetValue(weapon) end)
    return ok and (tonumber(value) or 0) or 0
end

local function SetTryingToCharge(weapon, value)
    if setterModes.tryingToCharge ~= "field" then
        local ok = pcall(function() weapon.tryingToCharge = value end)
        if ok then
            setterModes.tryingToCharge = "direct"
            return true
        end
        setterModes.tryingToCharge = "field"
    end

    local field = ResolveField(weapon, "tryingToCharge", "tryingToCharge")
    local ok = field ~= nil and pcall(function() field.SetValue(weapon, value) end)
    if not ok then ReportOnce("tryingToCharge", "cannot write tryingToCharge") end
    return ok == true
end

local function SetWeaponDamageMultiplier(weapon, value)
    value = Single(value)
    if setterModes.weaponDamage ~= "field" then
        local ok = pcall(function() weapon.WeaponDamageModifier = value end)
        if ok then
            setterModes.weaponDamage = "direct"
            return true
        end
        setterModes.weaponDamage = "field"
    end

    local field = ResolveField(
        weapon,
        "weaponDamage",
        "<WeaponDamageModifier>k__BackingField"
    )
    local ok = field ~= nil and pcall(function() field.SetValue(weapon, value) end)
    if not ok then ReportOnce("weaponDamage", "cannot write WeaponDamageModifier") end
    return ok == true
end

local function SetWeaponPenetration(weapon, value)
    value = Single(value)
    if setterModes.weaponPenetration ~= "field" then
        local ok = pcall(function() weapon.Penetration = value end)
        if ok then
            setterModes.weaponPenetration = "direct"
            return true
        end
        setterModes.weaponPenetration = "field"
    end

    local field = ResolveField(
        weapon,
        "weaponPenetration",
        "<Penetration>k__BackingField"
    )
    local ok = field ~= nil and pcall(function() field.SetValue(weapon, value) end)
    if not ok then ReportOnce("weaponPenetration", "cannot write RangedWeapon.Penetration") end
    return ok == true
end

local function SetProjectileSpread(projectile, value)
    value = Single(value)
    if setterModes.projectileSpread ~= "field" then
        local ok = pcall(function() projectile.Spread = value end)
        if ok then
            setterModes.projectileSpread = "direct"
            return true
        end
        setterModes.projectileSpread = "field"
    end

    local field = ResolveField(projectile, "projectileSpread", "<Spread>k__BackingField")
    local ok = field ~= nil and pcall(function() field.SetValue(projectile, value) end)
    if not ok then ReportOnce("projectileSpread", "cannot write Projectile.Spread") end
    return ok == true
end

local function IdentifierValue(item)
    if item == nil or item.Prefab == nil or item.Prefab.Identifier == nil then return "" end
    return item.Prefab.Identifier.Value or tostring(item.Prefab.Identifier)
end

local function IsTargetWeapon(weapon)
    if weapon == nil then return false end
    local identifier = IdentifierValue(weapon.item)
    return identifier == WEAPON_IDENTIFIER or identifier == ORACLE_IDENTIFIER
end

local function GetLoadedProjectile(weapon, state)
    local item = weapon.item
    if item == nil or item.Removed then return nil end
    -- This weapon has one magazine slot, and its magazine has one projectile slot.
    local inventory = item.OwnInventory
    local magazine = inventory ~= nil and inventory.GetItemAt(0) or nil
    local magazineInventory = magazine ~= nil and not magazine.Removed and magazine.OwnInventory or nil
    local round = magazineInventory ~= nil and magazineInventory.GetItemAt(0) or nil
    if round ~= nil and round.Removed then round = nil end

    if state.loadedRound ~= round or state.loadedMagazine ~= magazine then
        state.loadedMagazine = magazine
        state.loadedRound = round
        state.loadedProjectile = round ~= nil and round.GetComponentString("Projectile") or nil
    end
    return state.loadedProjectile
end

local function GetState(weapon)
    local state = states[weapon]
    if state == nil then
        state = {
            user = nil,
            charging = false,
            releasing = false,
            oracle = IdentifierValue(weapon.item) == ORACLE_IDENTIFIER,
            idleTuningTimer = 0
        }
        states[weapon] = state
    end
    return state
end

local function GetMaxChargeTime(weapon)
    local ok, value = pcall(function() return weapon.MaxChargeTime end)
    if not ok then return 0 end
    return tonumber(value) or 0
end

local function GetChargeRatio(weapon)
    local maxChargeTime = GetMaxChargeTime(weapon)
    if maxChargeTime <= 0 then return 1 end
    return math.max(0, math.min(1, GetCurrentChargeTime(weapon) / maxChargeTime))
end

local function ApplyChargeTuning(weapon, state, ratio, force)
    ratio = math.max(0, math.min(1, ratio or 0))
    if state.oracle then
        local damage = 1 + 0.5 * ratio
        if force or state.appliedDamage ~= damage then
            if SetWeaponDamageMultiplier(weapon, damage) then state.appliedDamage = damage end
        end
        return
    end
    local spread = UNCHARGED_SPREAD + (CHARGED_SPREAD - UNCHARGED_SPREAD) * ratio
    local damage = BASE_DAMAGE_MULTIPLIER +
        (MAX_DAMAGE_MULTIPLIER - BASE_DAMAGE_MULTIPLIER) * ratio
    local penetration = BASE_PENETRATION +
        (MAX_PENETRATION - BASE_PENETRATION) * ratio

    local projectile = GetLoadedProjectile(weapon, state)
    if projectile ~= nil and (force or projectileSpreads[projectile] ~= spread) then
        if SetProjectileSpread(projectile, spread) then projectileSpreads[projectile] = spread end
    end
    if force or state.appliedDamage ~= damage then
        if SetWeaponDamageMultiplier(weapon, damage) then state.appliedDamage = damage end
    end
    if force or state.appliedPenetration ~= penetration then
        if SetWeaponPenetration(weapon, penetration) then state.appliedPenetration = penetration end
    end
end

local function IsHeldBy(character, item)
    if character == nil or character.Removed or character.Inventory == nil then return false end
    local rightHand = character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand)
    local leftHand = character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand)
    return rightHand == item or leftHand == item
end

local function ResetCharge(weapon, state)
    state.user = nil
    state.charging = false
    state.releasing = false
    state.waitingForRound = false
    SetCurrentChargeTime(weapon, 0)
    SetTryingToCharge(weapon, false)
    ApplyChargeTuning(weapon, state, 0)
    state.idleTuningTimer = IDLE_TUNING_INTERVAL
end

local function EnsureOracleRound(magazine, onReady)
    if CLIENT and Game.IsMultiplayer then return false end
    if magazine == nil or magazine.Removed or magazine.Condition <= 0 then return false end
    local inventory = magazine.OwnInventory
    if inventory == nil then return false end
    local round = inventory.GetItemAt(0)
    if round ~= nil and not round.Removed then
        if onReady ~= nil then onReady() end
        return true
    end
    local callbacks = oracleRefills[magazine]
    if callbacks ~= nil then
        if onReady ~= nil then callbacks[#callbacks + 1] = onReady end
        return true
    end
    local prefab = ItemPrefab.GetItemPrefab(ORACLE_PRIMARY)
    if prefab == nil then
        ReportOnce("oracleRefill", "missing oracle primary projectile")
        return false
    end
    callbacks = onReady ~= nil and { onReady } or {}
    oracleRefills[magazine] = callbacks
    local generation = roundGeneration
    Entity.Spawner.AddItemToSpawnQueue(prefab, inventory, nil, nil, function(item)
        if generation ~= roundGeneration then return end
        oracleRefills[magazine] = nil
        if item ~= nil and not item.Removed and
            (magazine.Removed or item.ParentInventory ~= inventory) then
            Entity.Spawner.AddItemToRemoveQueue(item)
        end
        for _, callback in ipairs(callbacks) do callback() end
    end, true)
    return true
end

local function FireChargedShot(weapon, state, deltaTime, releasedRatio)
    local user = state.user
    local ratio = releasedRatio or GetChargeRatio(weapon)
    local maxChargeTime = GetMaxChargeTime(weapon)

    local oracleShot = nil
    if state.oracle then
        if maxChargeTime >= 999 or (tonumber(weapon.ReloadTimer) or 0) > 0 then
            ResetCharge(weapon, state)
            return
        end
        local identifier, cost = ORACLE_PRIMARY, 20
        if ratio < 0.3 then
            identifier, cost = ORACLE_TIER_TWO, 5
        elseif ratio < 0.6 then
            identifier, cost = ORACLE_TIER_ONE, 10
        end
        local inventory = weapon.item.OwnInventory
        local magazine = inventory ~= nil and inventory.GetItemAt(0) or nil
        if magazine == nil or magazine.Removed or IdentifierValue(magazine) ~= ORACLE_MAGAZINE
            or magazine.Condition < cost then
            ResetCharge(weapon, state)
            return
        end
        local projectile = GetLoadedProjectile(weapon, state)
        if projectile == nil then
            if CLIENT and Game.IsMultiplayer then
                ResetCharge(weapon, state)
                return
            end
            state.waitingForRound = true
            SetTryingToCharge(weapon, false)
            local queued = EnsureOracleRound(magazine, function()
                if not state.waitingForRound then return end
                state.waitingForRound = false
                if not IsHeldBy(user, weapon.item) or magazine.Removed or
                    inventory.GetItemAt(0) ~= magazine or GetLoadedProjectile(weapon, state) == nil then
                    ResetCharge(weapon, state)
                    return
                end
                FireChargedShot(weapon, state, deltaTime, ratio)
            end)
            if not queued then ResetCharge(weapon, state) end
            return
        end
        if projectile.IsActive or projectile.IsStuckToTarget then
            ResetCharge(weapon, state)
            return
        end
        oracleShot = { identifier = identifier, cost = cost, magazine = magazine, consumed = false }
        oracleShots[weapon.item] = oracleShot
    end

    -- Revalidate ammunition and values at release, even between idle checks.
    ApplyChargeTuning(weapon, state, ratio, true)
    SetCurrentChargeTime(weapon, maxChargeTime)
    state.releasing = true

    local ok, errorMessage = pcall(function()
        weapon.item.Use(deltaTime, user, nil, nil, user)
    end)

    if oracleShot ~= nil then
        oracleShots[weapon.item] = nil
    end
    state.releasing = false
    ResetCharge(weapon, state)
    if not ok then ReportOnce("release", "release shot failed: " .. tostring(errorMessage)) end
end

local function ScheduleTapReleaseCheck(weapon, state)
    local generation = roundGeneration
    Timer.Wait(function()
        if generation ~= roundGeneration then return end
        if not state.charging or state.releasing or state.waitingForRound then return end
        if weapon == nil then
            state.user = nil
            state.charging = false
            state.releasing = false
            return
        end
        if weapon.item == nil or weapon.item.Removed or not IsHeldBy(state.user, weapon.item) then
            ResetCharge(weapon, state)
            return
        end

        if not state.user.IsKeyDown(InputType.Shoot) then
            FireChargedShot(weapon, state, Single(TAP_RELEASE_DELTA_TIME))
        end
    end, TAP_RELEASE_CHECK_DELAY)
end

local useParameters = { "System.Single", "Barotrauma.Character" }
local updateParameters = { "System.Single", "Barotrauma.Camera" }

-- The native shot supplies the muzzle, aim, quality multiplier and collision exclusions.
-- Replace only low/mid-charge primary bolts; the spawned tier retains its own impact effects.
Hook.Patch(
    "aic_oracle_charge_projectile",
    "Barotrauma.Items.Components.Projectile",
    "Shoot",
    function(instance, ptable)
        if IdentifierValue(instance.Item) ~= ORACLE_PRIMARY then return end
        local launcher = instance.Launcher
        local shot = launcher ~= nil and oracleShots[launcher] or nil
        if shot == nil then return end
        if shot.identifier == ORACLE_PRIMARY then
            oracleProjectileShots[instance] = shot
            return
        end

        ptable.PreventExecution = true
        if CLIENT and Game.IsMultiplayer then return end

        local prefab = ItemPrefab.GetItemPrefab(shot.identifier)
        if prefab == nil then
            ReportOnce(shot.identifier, "missing oracle projectile: " .. shot.identifier)
            return
        end
        local source = instance.Item
        local submarine = source.Submarine
        local weaponPos, spawnPos = ptable["weaponPos"], ptable["spawnPos"]
        weaponPos = Vector2(weaponPos.X, weaponPos.Y)
        spawnPos = Vector2(spawnPos.X, spawnPos.Y)
        local rotation = ptable["rotation"]
        local user = ptable["user"]
        local attacker = instance.Attacker or user
        local ignoredBodies = ptable["ignoredBodies"]
        local damageMultiplier = ptable["damageMultiplier"]
        local launchImpulse = ptable["launchImpulseModifier"]
        local position = ConvertUnits.ToDisplayUnits(spawnPos)
        local generation = roundGeneration
        local function OnSpawned(item)
            if item == nil or item.Removed then return end
            if generation ~= roundGeneration then
                Entity.Spawner.AddItemToRemoveQueue(item)
                return
            end
            local projectile = item.GetComponentString("Projectile")
            if projectile == nil then
                Entity.Spawner.AddItemToRemoveQueue(item)
                return
            end
            projectile.Launcher = launcher
            projectile.Attacker = attacker
            oracleProjectileShots[projectile] = shot
            projectile.Shoot(user, weaponPos, spawnPos, rotation, ignoredBodies, true,
                damageMultiplier, launchImpulse)
        end
        if submarine ~= nil then
            Entity.Spawner.AddItemToSpawnQueue(prefab, position, submarine, nil, nil, OnSpawned)
        else
            Entity.Spawner.AddItemToSpawnQueue(prefab, position, nil, nil, OnSpawned)
        end
        -- Skipping the native Shoot also skips its Drop; free the magazine before refilling.
        source.Drop(nil)
        Entity.Spawner.AddItemToRemoveQueue(source)
    end,
    Hook.HookMethodType.Before
)

Hook.Patch(
    "aic_oracle_charge_projectile_fired",
    "Barotrauma.Items.Components.Projectile",
    "Shoot",
    function(instance, ptable)
        local shot = oracleProjectileShots[instance]
        if shot == nil then return end
        oracleProjectileShots[instance] = nil
        if CLIENT and Game.IsMultiplayer then return end
        -- Shoot can return without launching an already active/stuck projectile.
        if not instance.IsActive or shot.consumed or shot.magazine.Removed then return end
        shot.magazine.Condition = math.max(0, shot.magazine.Condition - shot.cost)
        shot.consumed = true
        EnsureOracleRound(shot.magazine)
    end,
    Hook.HookMethodType.After
)

Hook.Add("aic_oracle_charge_ammo", "AIC.OracleCharge.Ammo", function(effect, deltaTime, item)
    if CLIENT and Game.IsMultiplayer then return end
    if item == nil or item.Removed or IdentifierValue(item) ~= ORACLE_MAGAZINE then return end
    EnsureOracleRound(item)
end)

Hook.Patch(
    "aic_charge_spread_use_v2",
    "Barotrauma.Items.Components.RangedWeapon",
    "Use",
    useParameters,
    function(instance, ptable)
        if not IsTargetWeapon(instance) then return end

        local state = GetState(instance)
        if state.releasing then return end
        if state.waitingForRound then
            ptable.PreventExecution = true
            return false
        end

        local character = ptable["character"]
        if character == nil or character.Removed then
            ResetCharge(instance, state)
            return
        end

        local startedCharging = not state.charging
        state.user = character
        state.charging = true
        SetTryingToCharge(instance, true)

        if startedCharging then
            ApplyChargeTuning(instance, state, GetChargeRatio(instance))
            ScheduleTapReleaseCheck(instance, state)
        end

        ptable.PreventExecution = true
        return false
    end,
    Hook.HookMethodType.Before
)

Hook.Patch(
    "aic_charge_spread_update_before_v2",
    "Barotrauma.Items.Components.RangedWeapon",
    "Update",
    updateParameters,
    function(instance, ptable)
        if not IsTargetWeapon(instance) then return end

        local state = GetState(instance)
        if not state.charging then return end
        if instance.item == nil or instance.item.Removed or
            not IsHeldBy(state.user, instance.item) then
            ResetCharge(instance, state)
            return
        end
        if state.waitingForRound then return end

        local shootHeld = state.user.IsKeyDown(InputType.Shoot)
        local botReady = state.user.IsBot and GetChargeRatio(instance) >= 1
        if not shootHeld or botReady then
            FireChargedShot(instance, state, ptable["deltaTime"])
        end
    end,
    Hook.HookMethodType.Before
)

Hook.Patch(
    "aic_charge_spread_update_after_v2",
    "Barotrauma.Items.Components.RangedWeapon",
    "Update",
    updateParameters,
    function(instance, ptable)
        if not IsTargetWeapon(instance) then return end
        local state = GetState(instance)
        if state.charging then
            ApplyChargeTuning(instance, state, GetChargeRatio(instance))
        else
            state.idleTuningTimer = state.idleTuningTimer - (tonumber(ptable["deltaTime"]) or 0)
            if state.idleTuningTimer <= 0 then
                ApplyChargeTuning(instance, state, 0)
                state.idleTuningTimer = IDLE_TUNING_INTERVAL
            end
        end
    end,
    Hook.HookMethodType.After
)

Hook.Add("roundEnd", "aic_charge_spread_clear_v2", function()
    roundGeneration = roundGeneration + 1
    states = setmetatable({}, { __mode = "k" })
    projectileSpreads = setmetatable({}, { __mode = "k" })
    oracleShots = setmetatable({}, { __mode = "k" })
    oracleProjectileShots = setmetatable({}, { __mode = "k" })
    oracleRefills = setmetatable({}, { __mode = "k" })
end)
