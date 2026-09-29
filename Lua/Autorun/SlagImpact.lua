local processed = setmetatable({}, { __mode = "k" })
local generation = 0

local function SpawnBurns(item, prefab, hit, count)
    local source = item.GetComponentString("Projectile")
    if source == nil or item.body == nil then return end
    local submarine = item.Submarine
    local epoch = generation
    local user, attacker, launcher = source.User, source.Attacker, source.Launcher
    local heading = item.body.Rotation
    local dx, dy = math.cos(heading), math.sin(heading)
    -- Start on the incoming side of the contact. Shoot checks the path to each offset
    -- so scattering cannot place a droplet through a nearby wall.
    local origin = Vector2(hit.X - dx * 14, hit.Y - dy * 14)
    if submarine ~= nil then
        origin = Vector2(origin.X - submarine.Position.X, origin.Y - submarine.Position.Y)
    end
    local originSim = ConvertUnits.ToSimUnits(origin)
    local center = (count + 1) / 2
    for i = 1, count do
        local offset = (i - center) * 40
        local position = Vector2(origin.X - dy * offset, origin.Y + dx * offset)
        local spawnSim = ConvertUnits.ToSimUnits(position)
        local angle = heading + (i - center) * 0.65
        local function Spawned(burn)
            if burn == nil or burn.Removed then return end
            if epoch ~= generation or (submarine ~= nil and submarine.Removed) then
                Entity.Spawner.AddItemToRemoveQueue(burn)
                return
            end
            local projectile = burn.GetComponentString("Projectile")
            if projectile == nil then
                Entity.Spawner.AddItemToRemoveQueue(burn)
                return
            end
            projectile.Attacker = attacker ~= nil and not attacker.Removed and attacker or nil
            projectile.Launcher = launcher ~= nil and not launcher.Removed and launcher or nil
            local shooter = user ~= nil and not user.Removed and user or nil
            projectile.Shoot(shooter, originSim, spawnSim, angle, projectile.IgnoredBodies, true, 1, 0)
        end
        if submarine ~= nil then
            Entity.Spawner.AddItemToSpawnQueue(prefab, position, submarine, nil, nil, Spawned)
        else
            Entity.Spawner.AddItemToSpawnQueue(prefab, position, nil, nil, Spawned)
        end
    end
end

Hook.Add("aic_slag_impact_burn", "AIC.Slag.Impact", function(effect, deltaTime, item, targets, worldPosition)
    if CLIENT and Game.IsMultiplayer then return end
    if item == nil or item.Removed or processed[item] then return end
    local identifier = tostring(item.Prefab.Identifier.Value)
    local isDragon = identifier == "aic_faithguard_dragon_round"
        or identifier == "aic_faithguard_molten_thermal_round"
    local prefab = ItemPrefab.GetItemPrefab(isDragon and "aic_faithguard_dragon_burn"
        or (identifier == "aic_faithguard_brand_round" and "aic_faithguard_brand_burn" or "aic_faithguard_slag_burn"))
    if prefab == nil or Entity.Spawner == nil then return end
    processed[item] = true

    -- Preserve one droplet for slag/brand, three for dragon and five for molten.
    local count = identifier == "aic_faithguard_molten_thermal_round" and 5 or (isDragon and 3 or 1)
    local hit = worldPosition or item.WorldPosition
    SpawnBurns(item, prefab, Vector2(hit.X, hit.Y), count)
end)

Hook.Add("roundEnd", "AIC.Slag.Reset", function()
    processed = setmetatable({}, { __mode = "k" })
    generation = generation + 1
end)
