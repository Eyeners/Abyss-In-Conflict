local descriptor = Descriptors["Barotrauma.Items.Components.RangedWeapon"]
LuaUserData.MakePropertyAccessible(descriptor, "ReloadTimer")
local states = setmetatable({}, { __mode = "k" })
local generation = 0
local sound
local modPath = ST_Lua.Path
local THERMAL_LAUNCHER = "aic_faithguard_molten_thermal_launcher"
local thermalSounds = {}

local function FindThermalFuel(item)
    if item == nil or item.Removed or item.OwnInventory == nil then return nil end
    for slot = 0, 5 do
        local fuel = item.OwnInventory.GetItemAt(slot)
        if fuel ~= nil and not fuel.Removed and fuel.HasTag("weldingtoolfuel") and fuel.Condition >= 50 then
            return fuel
        end
    end
end

Hook.Add("aic_thermal_launcher_fuel", "AIC.ThermalLauncher.Fuel", function(effect, deltaTime, item)
    if CLIENT and Game.IsMultiplayer then return end
    if item == nil or tostring(item.Prefab.Identifier.Value) ~= THERMAL_LAUNCHER then return end
    local fuel = FindThermalFuel(item)
    if fuel ~= nil then fuel.Condition = fuel.Condition - 50 end
end)

local function QueueThermalSound(item, state, at, epoch, finish, serial, fuel)
    if not CLIENT then return end
    Timer.Wait(function()
        if generation ~= epoch or item.Removed or states[item] ~= state then return end
        if finish then
            -- A later insertion extends this batch and cancels its previous ending sound.
            if state.serial ~= serial or next(state.rounds) == nil then return end
        elseif fuel.Removed or not state.rounds[fuel] then
            return
        end
        local key = (finish and "ReloadEnd" or "Reload") .. math.random(1, 3)
        if thermalSounds[key] == nil then
            thermalSounds[key] = Game.SoundManager.LoadSound(modPath .. "/践行者/音效/火焰喷射器" .. key .. ".ogg")
        end
        SoundPlayer.PlaySound(thermalSounds[key], item.WorldPosition, 0.8, 200, 1)
    end, math.max(0, (at - Timer.GetTime()) * 1000))
end

-- Resolve the actual firing hand; never apply the arm injury on both network peers.
Hook.Add("aic_forgehammer_recoil_penalty", "AIC.Forgehammer.RecoilPenalty", function(effect, deltaTime, item)
    if CLIENT and Game.IsMultiplayer then return end
    if item == nil or item.Removed or item.ParentInventory == nil then return end
    local character = item.ParentInventory.Owner
    if character == nil or character.Inventory == nil or character.AnimController == nil then return end
    local armType
    if character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) == item then
        armType = LimbType.RightArm
    elseif character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) == item then
        armType = LimbType.LeftArm
    else
        return
    end
    local skill = character.GetSkillLevel("weapons")
    if skill >= 40 then return end
    local arm = character.AnimController.GetLimb(armType)
    if arm == nil then return end
    local damage = skill >= 30 and 2 or (skill >= 20 and 3 or 4)
    local stun = skill >= 30 and 0.3 or (skill >= 20 and 0.6 or 1)
    character.CharacterHealth.ApplyAffliction(arm, AfflictionPrefab.Prefabs["blunttrauma"].Instantiate(damage, character), true, false, false)
    character.CharacterHealth.ApplyAffliction(nil, AfflictionPrefab.Prefabs["stun"].Instantiate(stun, character), true, false, false)
end)

local function QueueSound(item, state, at, epoch)
    if not CLIENT then return end
    Timer.Wait(function()
        if generation ~= epoch or item.Removed or states[item] ~= state then return end
        if sound == nil then
            sound = Game.SoundManager.LoadSound(modPath .. "/Items/Weapons/gavel_shell_insert.ogg")
        end
        SoundPlayer.PlaySound(sound, item.WorldPosition, 0.7, 200, 1)
    end, math.max(0, (at - Timer.GetTime()) * 1000))
end

-- Also called synchronously at the firing boundary: AI can load and use in one frame.
local function Reconcile(item)
    if item == nil or item.Removed then return end
    local id = tostring(item.Prefab.Identifier.Value)
    local gavel = id == "aic_gdf_gavelrotary_grenadelauncher"
    local forgehammer = id == "aic_gdf_forgehammer_shotgun"
    local slag = id == "aic_faithguard_slag_recon_rifle" or id == "aic_faithguard_brand_pistol"
    local dragon = id == "aic_faithguard_dragon_flamethrower"
    local thermal = id == THERMAL_LAUNCHER
    if not gavel and not forgehammer and not slag and not dragon and not thermal and id ~= "aic_gdf_cluster_rocket_launcher" then return end
    local weapon = item.GetComponentString("RangedWeapon")
    if weapon == nil or item.OwnInventory == nil then return end
    local state = states[item]
    if state == nil then
        state = { rounds = {}, ready = 0, serial = 0 }
        states[item] = state
    end
    local epoch = generation
        local current, added, addedFuel = {}, 0, {}
        for round in item.OwnInventory.AllItemsMod do
            if not round.Removed and (not (dragon or thermal) or round.HasTag("weldingtoolfuel")) then
                current[round] = true
                if not state.rounds[round] then
                    added = added + 1
                    if thermal then addedFuel[#addedFuel + 1] = round end
                end
            end
        end
        state.rounds = current
        if added == 0 then return state end
        local now = Timer.GetTime()
        if thermal then
            state.serial = state.serial + 1
            for _, fuel in ipairs(addedFuel) do
                local start = math.max(now, state.ready)
                QueueThermalSound(item, state, start, epoch, false, state.serial, fuel)
                state.ready = start + 0.3
            end
            QueueThermalSound(item, state, state.ready, epoch, true, state.serial)
        elseif gavel or forgehammer then
            local shellDelay = forgehammer and 0.1 or 0.2
            for index = 1, added do
                state.ready = math.max(now, state.ready) + shellDelay
                QueueSound(item, state, state.ready, epoch)
            end
        else
            state.ready = now + ((slag or dragon) and 1 or 2)
            if not dragon then QueueSound(item, state, state.ready, epoch) end
        end
        -- Preserve the shot cooldown without adding it to the reload duration.
        weapon.ReloadTimer = math.max(tonumber(weapon.ReloadTimer) or 0, state.ready - now)
        weapon.IsActive = true
    return state
end

Hook.Add("aic_ammo_insertion_delay", "AIC.AmmoInsertionDelay", function(effect, deltaTime, item)
    local epoch = generation
    -- Wait for removal/insertion batches to finish; Use performs the same check immediately.
    Timer.Wait(function()
        if epoch == generation then Reconcile(item) end
    end, 0)
end)

Hook.Patch("aic_ammo_insertion_fire_gate", "Barotrauma.Items.Components.RangedWeapon", "Use",
    { "System.Single", "Barotrauma.Character" }, function(instance, ptable)
        local state = Reconcile(instance.Item)
        if state == nil then return end
        local remaining = state.ready - Timer.GetTime()
        if remaining > 0 then
            instance.ReloadTimer = math.max(tonumber(instance.ReloadTimer) or 0, remaining)
            instance.IsActive = true
            ptable.PreventExecution = true
            return false
        end
        if tostring(instance.Item.Prefab.Identifier.Value) == THERMAL_LAUNCHER and FindThermalFuel(instance.Item) == nil then
            ptable.PreventExecution = true
            return false
        end
    end, Hook.HookMethodType.Before)

Hook.Add("roundEnd", "AIC.AmmoInsertionDelay.Reset", function()
    generation = generation + 1
    states = setmetatable({}, { __mode = "k" })
end)
