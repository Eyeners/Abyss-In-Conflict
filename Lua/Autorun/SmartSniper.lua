local ID = "aic_smart_sniper"
local MODE = "aic_smart_sniper_enabled"
local HELMET_ID = "aic_foresight_aim_assist_helmet"
local HELMET_MODE = "aic_foresight_aim_assist_enabled"
local INTERVAL = 1
local RANGE = 3000
local CONE = math.cos(math.rad(12))
local PENETRATION = 0.2
local VALIDITY_INTERVAL = 0.05
local states = setmetatable({}, { __mode = "k" })
local function authoritative() return SERVER or not Game.IsMultiplayer end
-- A binding setup failure must not prevent the mode button registering.
local bindingOK, bindingError = pcall(function()
    LuaUserData.MakeFieldAccessible(Descriptors["Barotrauma.Limb"], "WearingItems")
end)
if not bindingOK then print("[AIC] Smart sniper binding: " .. tostring(bindingError)) end
local vitalityBindingOK, vitalityBindingError = pcall(function()
    LuaUserData.MakeFieldAccessible(Descriptors["Barotrauma.CharacterHealth"], "limbHealths")
    local descriptor = LuaUserData.RegisterType("Barotrauma.CharacterHealth+LimbHealth")
    LuaUserData.MakeFieldAccessible(descriptor, "VitalityMultipliers")
    LuaUserData.MakeFieldAccessible(descriptor, "VitalityTypeMultipliers")
end)
local vitalityWarning = false
if not vitalityBindingOK then
    print("[AIC] Smart sniper vitality binding: " .. tostring(vitalityBindingError))
    vitalityWarning = true
end

local function V(x, y) return Vector2(x, y) end
local function sub(a, b) return V(a.X - b.X, a.Y - b.Y) end
local function add(a, b) return V(a.X + b.X, a.Y + b.Y) end
local function length(v) return math.sqrt(v.X * v.X + v.Y * v.Y) end
local function ours(item)
    return item and not item.Removed and item.Prefab
        and string.lower(tostring(item.Prefab.Identifier.Value)) == ID
end
local function held(item, user)
    if not user or user.Removed or user.IsDead or not user.Inventory then return false end
    return user.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) == item
        or user.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) == item
end
local function profile(item, user)
    if not item or item.Removed or not held(item, user)
        or not item.GetComponentString("RangedWeapon") then return nil end
    local helmet = user.Inventory.GetItemInLimbSlot(InvSlotType.Head)
    if helmet and not helmet.Removed and helmet.Condition > 0 and helmet.HasTag(HELMET_MODE)
        and tostring(helmet.Prefab.Identifier.Value) == HELMET_ID then return "head" end
    if ours(item) and item.HasTag(MODE) then return "weakpoint" end
    return nil
end
local function eligible(limb, mode)
    return mode ~= "head" or limb.type == LimbType.Head
end
local function stateFor(item)
    if not states[item] then states[item] = { nextScan = 0 } end
    return states[item]
end
local function enemy(user, target)
    return target ~= user and not target.Removed and target.Enabled and not target.IsDead
        and not target.IsIncapacitated and not user.IsOnFriendlyTeam(target)
end
local function live(limb)
    return limb and not limb.Removed and not limb.IsSevered and limb.body and limb.body.Enabled
        and limb.HealthIndex >= 0 and limb.character and not limb.character.Removed
end
local function aim(user)
    if CLIENT and user == Character.Controlled and Screen.Selected and Screen.Selected.Cam then
        return Screen.Selected.Cam.ScreenToWorld(PlayerInput.MousePosition)
    end
    return user.CursorWorldPosition
end
local function muzzle(weapon)
    return add(weapon.item.WorldPosition, ConvertUnits.ToDisplayUnits(weapon.TransformedBarrelPos))
end

-- Visibility checks use both submarine coordinate systems when crossing between subs.
local function visible(item, from, limb)
    local delta = ConvertUnits.ToSimUnits(sub(limb.WorldPosition, from))
    local start = add(item.SimPosition, ConvertUnits.ToSimUnits(sub(from, item.WorldPosition)))
    local function clear(a, b)
        return Submarine.CheckVisibility(a, b, false, false, true, true, true) == nil
    end
    if item.Submarine == limb.Submarine or limb.Submarine == nil then
        return clear(start, add(start, delta))
    end
    if item.Submarine == nil then
        return clear(limb.SimPosition, sub(limb.SimPosition, delta))
    end
    return clear(start, add(start, delta)) and clear(limb.SimPosition, sub(limb.SimPosition, delta))
end

local function aimRay(from, cursor)
    local dx, dy = cursor.X - from.X, cursor.Y - from.Y
    local distance = math.sqrt(dx * dx + dy * dy)
    if distance < 1 then return nil end
    return dx / distance, dy / distance
end

local function coneDistance(from, rayX, rayY, point)
    local dx, dy = point.X - from.X, point.Y - from.Y
    local distanceSquared = dx * dx + dy * dy
    if distanceSquared < 1 or distanceSquared > RANGE * RANGE then return nil end
    local dot = rayX * dx + rayY * dy
    if dot < 0 or dot * dot < CONE * CONE * distanceSquared then return nil end
    return distanceSquared, dx, dy
end

local gunshotIdentifier, damageIdentifier
local function vitalityWeight(limb, weights)
    if not vitalityBindingOK then return 1 end
    local ok, weight = pcall(function()
        local characterHealth = limb.character.CharacterHealth
        local cached = weights[characterHealth]
        if cached and cached[limb.HealthIndex] ~= nil then return cached[limb.HealthIndex] end
        local healths = characterHealth.limbHealths
        if limb.HealthIndex < 0 or limb.HealthIndex >= healths.Count then return 1 end
        local health = healths[limb.HealthIndex]
        local function lookup(values, identifier)
            if values.ContainsKey(identifier) then return values[identifier] end
            return 1
        end
        gunshotIdentifier = gunshotIdentifier or Identifier("gunshotwound")
        damageIdentifier = damageIdentifier or Identifier("damage")
        -- Match CharacterHealth.GetVitalityMultiplier: identifier and type both apply.
        local value = lookup(health.VitalityMultipliers, gunshotIdentifier)
            * lookup(health.VitalityTypeMultipliers, damageIdentifier)
        if not cached then cached = {}; weights[characterHealth] = cached end
        cached[limb.HealthIndex] = value
        return value
    end)
    if ok then return weight end
    if not vitalityWarning then
        print("[AIC] Smart sniper vitality weighting: " .. tostring(weight))
        vitalityWarning = true
    end
    return 1
end

-- Rank expected vitality loss, without applying test damage or multiplying the real shot twice.
-- Global affliction resistance is common to the character and does not change limb ordering.
local function vulnerability(limb, from, weights)
    local hit = add(limb.SimPosition, ConvertUnits.ToSimUnits(sub(from, limb.WorldPosition)))
    local multiplier = 1
    local function apply(modifiers)
        for modifier in modifiers do
            if modifier.MatchesAffliction("gunshotwound", "damage")
                and limb.SectorHit(modifier.ArmorSectorInRadians, hit) then
                local value = modifier.DamageMultiplier
                if modifier.DeflectProjectiles and value < 1 then
                    value = value + (1 - value) * PENETRATION
                end
                multiplier = multiplier * value * math.min(1, math.max(0, modifier.ProbabilityMultiplier))
            end
        end
    end
    apply(limb.DamageModifiers)
    for wearable in limb.WearingItems do apply(wearable.WearableComponent.DamageModifiers) end
    return multiplier * vitalityWeight(limb, weights)
end

local function scan(weapon, user, state)
    state.nextScan = Timer.GetTime() + INTERVAL
    state.limb = nil
    state.validity = nil
    local from, cursor = muzzle(weapon), aim(user)
    local rayX, rayY = aimRay(from, cursor)
    if not rayX then return end
    local candidates = {}
    local weights = {}
    for target in Character.CharacterList do
        if enemy(user, target) and target.AnimController then
            local bestOffset = math.huge
            for limb in target.AnimController.Limbs do
                if live(limb) then
                    local distanceSquared, dx, dy = coneDistance(from, rayX, rayY, limb.WorldPosition)
                    if distanceSquared then
                        local offset = math.abs(rayX * dy - rayY * dx)
                        bestOffset = math.min(bestOffset, offset + math.sqrt(distanceSquared) * 0.02)
                    end
                end
            end
            if bestOffset < math.huge then candidates[#candidates + 1] = { actor = target, score = bestOffset } end
        end
    end
    table.sort(candidates, function(a, b)
        if a.score == b.score then return a.actor.ID < b.actor.ID end
        return a.score < b.score
    end)
    -- Select a character near the raw cursor first, then its weakest visible limb.
    for _, candidate in ipairs(candidates) do
        local ranked = {}
        for limb in candidate.actor.AnimController.Limbs do
            if live(limb) and eligible(limb, state.profile)
                and length(sub(limb.WorldPosition, from)) <= RANGE then
                ranked[#ranked + 1] = { limb = limb,
                    value = state.profile == "head" and 1 or vulnerability(limb, from, weights), order = #ranked + 1 }
            end
        end
        table.sort(ranked, function(a, b)
            if a.value == b.value then return a.order < b.order end
            return a.value > b.value
        end)
        for _, entry in ipairs(ranked) do
            if visible(weapon.item, from, entry.limb) then state.limb = entry.limb; return end
        end
    end
end

local function valid(weapon, user, state, checkWall)
    if not weapon or not user or not user.IsKeyDown(InputType.Aim)
        or profile(weapon.item, user) ~= state.profile or state.user ~= user then
        state.validity = nil
        return false
    end
    local limb = state.limb
    if not live(limb) or not eligible(limb, state.profile)
        or limb.character.IsDead or limb.character.IsIncapacitated or not limb.character.Enabled then
        state.validity = nil
        return false
    end
    local now = Timer.GetTime()
    local cached = state.validity
    if not checkWall and cached and cached.limb == limb and now < cached.expires
        and cached.userTeam == user.TeamID and cached.targetTeam == limb.character.TeamID then
        return cached.result
    end
    local function result(value)
        if not checkWall then
            state.validity = { limb = limb, result = value, expires = now + VALIDITY_INTERVAL,
                userTeam = user.TeamID, targetTeam = limb.character.TeamID }
        end
        return value
    end
    if not enemy(user, limb.character) then return result(false) end
    local from = muzzle(weapon)
    if checkWall then
        local muzzleSim = add(weapon.item.SimPosition, weapon.TransformedBarrelPos)
        if Submarine.CheckVisibility(user.AnimController.AimSourceSimPos, muzzleSim,
            false, false, true, true, true) ~= nil then return false end
    end
    if length(sub(limb.WorldPosition, from)) > RANGE then return result(false) end
    -- The cone retains the character, not the selected weak point above/below the cursor.
    local cursor = aim(user)
    local rayX, rayY = aimRay(from, cursor)
    if not rayX then return result(false) end
    local targetInCone = false
    for targetLimb in limb.character.AnimController.Limbs do
        if live(targetLimb) and coneDistance(from, rayX, rayY, targetLimb.WorldPosition) then
            targetInCone = true
            break
        end
    end
    return result(targetInCone and (not checkWall or visible(weapon.item, from, limb)))
end

local function setMode(item, enabled)
    if not ours(item) then
        print("[AIC] Smart sniper: mode hook received an unexpected item")
        return
    end
    if not authoritative() then return end
    -- TickBox effects may repeat while selected; only actual transitions reset the cache.
    if item.HasTag(MODE) == enabled then return end
    if enabled then item.AddTag(MODE) else item.RemoveTag(MODE) end
    states[item] = nil
    if SERVER and Game.IsMultiplayer then
        local property = item.SerializableProperties[Identifier("Tags")]
        Networking.CreateEntityEvent(item, Item.ChangePropertyEventData(property, item))
    end
end
Hook.Add("aic_smart_sniper_on", "AIC.SmartSniper.On", function(_, _, item) setMode(item, true) end)
Hook.Add("aic_smart_sniper_off", "AIC.SmartSniper.Off", function(_, _, item) setMode(item, false) end)

local function setHelmetMode(item, enabled)
    if not item or item.Removed or not item.Prefab or not authoritative()
        or tostring(item.Prefab.Identifier.Value) ~= HELMET_ID then return end
    if item.HasTag(HELMET_MODE) == enabled then return end
    if enabled then item.AddTag(HELMET_MODE) else item.RemoveTag(HELMET_MODE) end
    if SERVER and Game.IsMultiplayer then
        local property = item.SerializableProperties[Identifier("Tags")]
        Networking.CreateEntityEvent(item, Item.ChangePropertyEventData(property, item))
    end
end
Hook.Add("aic_aim_helmet_on", "AIC.AimHelmet.On", function(_, _, item) setHelmetMode(item, true) end)
Hook.Add("aic_aim_helmet_off", "AIC.AimHelmet.Off", function(_, _, item) setHelmetMode(item, false) end)

local nextPoll = 0
local scanUser = nil
if CLIENT then
    Hook.Add("think", "AIC.SmartSniper.Scan", function()
        local user = Character.Controlled
        if user ~= scanUser then
            states = setmetatable({}, { __mode = "k" })
            scanUser = user
            nextPoll = 0
        end
        if not user or not user.Inventory or user.Removed or user.IsDead then return end
        local now = Timer.GetTime()
        if now < nextPoll then return end
        nextPoll = now + 0.5
        local seen = {}
        for _, slot in ipairs({ InvSlotType.RightHand, InvSlotType.LeftHand }) do
            local item = user.Inventory.GetItemInLimbSlot(slot)
            if item and not seen[item] then
                seen[item] = true
                local mode = profile(item, user)
                if not mode or not user.IsKeyDown(InputType.Aim) then
                    states[item] = nil
                else
                    local state = stateFor(item)
                    if state.profile ~= mode or state.user ~= user then
                        state.profile = mode
                        state.user = user
                        state.limb = nil
                        state.nextScan = 0
                    end
                    if now >= state.nextScan then
                        local ok, err = pcall(scan, item.GetComponentString("RangedWeapon"), user, state)
                        if not ok then
                            state.limb = nil
                            if not state.warned then
                                print("[AIC] Smart sniper scan: " .. tostring(err))
                                state.warned = true
                            end
                        end
                    end
                end
            end
        end
    end)
end

-- Remove the old no-lock fire gate, including when this script is reloaded in place.
Hook.RemovePatch("AIC.SmartSniper.Use", "Barotrauma.Items.Components.RangedWeapon",
    "Use", Hook.HookMethodType.Before)

-- Smart mode only adjusts aim; firing, hits and ammunition stay entirely native.
Hook.RemovePatch("AIC.SmartSniper.Shoot", "Barotrauma.Items.Components.Projectile",
    "Shoot", Hook.HookMethodType.Before)

if CLIENT then
    LuaUserData.MakeFieldAccessible(Descriptors["Barotrauma.Items.Components.RangedWeapon"], "crosshairPos")
    local originalCrosshairPositions = setmetatable({}, { __mode = "k" })
    Hook.Patch("Barotrauma.Character", "ControlLocalPlayer", function(user)
        if user ~= Character.Controlled or not user.IsKeyDown(InputType.Aim) or not user.Inventory then return end
        local item = user.Inventory.GetItemInLimbSlot(InvSlotType.RightHand)
        local state = item and states[item]
        if not state or not valid(item.GetComponentString("RangedWeapon"), user, state, false) then
            local left = user.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand)
            if left == item then return end
            item = left
            state = item and states[item]
            if not state or not valid(item.GetComponentString("RangedWeapon"), user, state, false) then return end
        end
        local pos = state.limb.WorldPosition
        if user.Submarine then pos = sub(pos, user.Submarine.Position) end
        user.CursorPosition = pos
    end, Hook.HookMethodType.After)

    Hook.Patch("AIC.SmartSniper.Crosshair", "Barotrauma.Items.Components.RangedWeapon", "DrawHUD",
        { "Microsoft.Xna.Framework.Graphics.SpriteBatch", "Barotrauma.Character" }, function(weapon, p)
            if p["character"] ~= Character.Controlled or not Screen.Selected then return end
            local state = states[weapon.item]
            if state and valid(weapon, p["character"], state, false) then
                originalCrosshairPositions[weapon] = weapon.crosshairPos
                weapon.crosshairPos = Screen.Selected.Cam.WorldToScreen(state.limb.WorldPosition)
            end
        end, Hook.HookMethodType.Before)

    Hook.Patch("AIC.SmartSniper.HUD", "Barotrauma.Items.Components.RangedWeapon", "DrawHUD",
        { "Microsoft.Xna.Framework.Graphics.SpriteBatch", "Barotrauma.Character" }, function(weapon, p)
            if originalCrosshairPositions[weapon] then
                weapon.crosshairPos = originalCrosshairPositions[weapon]
                originalCrosshairPositions[weapon] = nil
            end
        end, Hook.HookMethodType.After)
end
Hook.Add("roundEnd", "AIC.SmartSniper.Reset", function()
    states = setmetatable({}, { __mode = "k" }); nextPoll = 0; scanUser = nil
end)
