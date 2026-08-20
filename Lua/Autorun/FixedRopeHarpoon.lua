if CLIENT and Game.IsMultiplayer then return end

local LAUNCHER_IDENTIFIER = "aic_fixed_rope_harpoon_launcher"
local ANCHOR_IDENTIFIER = "aic_fixed_rope_harpoon_anchor"
local DEFAULT_PULL_FORCE = 600
local MIN_PULL_FORCE = 0
local MAX_PULL_FORCE = 5000

local configuredForce = {}
local activeAnchor = {}

local function IdentifierValue(item)
    if item == nil or item.Prefab == nil or item.Prefab.Identifier == nil then return "" end
    return item.Prefab.Identifier.Value
end

local function ConnectionName(connection)
    if connection == nil or connection.Name == nil then return "" end
    if connection.Name.Value ~= nil then return connection.Name.Value end
    return tostring(connection.Name)
end

local function SetAnchorForce(anchor, force)
    if anchor == nil or anchor.Removed then return end
    local rope = anchor.GetComponentString("Rope")
    if rope ~= nil then
        rope.TargetPullForce = force
    end
end

local function HandleSignal(signal, connection)
    if signal == nil or connection == nil or connection.Item == nil or connection.Item.Removed then return end

    local launcher = connection.Item
    local connectionName = ConnectionName(connection)

    if connectionName == "force_in" then
        local force = tonumber(signal.value)
        if force == nil then return end
        force = math.max(MIN_PULL_FORCE, math.min(MAX_PULL_FORCE, force))
        configuredForce[launcher] = force
        SetAnchorForce(activeAnchor[launcher], force)
    elseif connectionName == "release_in" and tostring(signal.value) ~= "0" then
        local anchor = activeAnchor[launcher]
        if anchor == nil or anchor.Removed then return end
        local rope = anchor.GetComponentString("Rope")
        if rope ~= nil then rope.Snapped = true end
    end
end

Hook.Add(
    "signalReceived." .. LAUNCHER_IDENTIFIER,
    "AIC.FixedRopeHarpoon.Signal",
    HandleSignal
)

Hook.Patch(
    "AIC.FixedRopeHarpoon.Configure",
    "Barotrauma.Items.Components.Turret",
    "Launch",
    function(instance, ptable)
        if instance == nil or IdentifierValue(instance.item) ~= LAUNCHER_IDENTIFIER then return end
        local anchor = ptable["projectile"]
        if IdentifierValue(anchor) ~= ANCHOR_IDENTIFIER then return end
        SetAnchorForce(anchor, configuredForce[instance.item] or DEFAULT_PULL_FORCE)
    end,
    Hook.HookMethodType.Before
)

Hook.Patch(
    "AIC.FixedRopeHarpoon.Track",
    "Barotrauma.Items.Components.Turret",
    "Launch",
    function(instance, ptable)
        if instance == nil or IdentifierValue(instance.item) ~= LAUNCHER_IDENTIFIER then return end
        local anchor = ptable["projectile"]
        if IdentifierValue(anchor) == ANCHOR_IDENTIFIER then
            activeAnchor[instance.item] = anchor
        end
    end,
    Hook.HookMethodType.After
)

Hook.Add("roundEnd", "AIC.FixedRopeHarpoon.Clear", function()
    configuredForce = {}
    activeAnchor = {}
end)
