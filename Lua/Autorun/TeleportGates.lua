if CLIENT and Game.IsMultiplayer then return end

local TELEPORT_COOLDOWN = 0.5
local lastTeleportTime = {}

local function TeleportCharacter(character, destination)
    if character == nil or character.Removed or character.IsDead then return end

    character.TeleportTo(destination)

    local selectedCharacter = character.SelectedCharacter
    if selectedCharacter ~= nil
        and not selectedCharacter.Removed
        and selectedCharacter ~= character then
        selectedCharacter.TeleportTo(destination)
    end
end

local function HandleTeleportSignal(signal, connection)
    if signal == nil
        or signal.sender == nil
        or connection == nil
        or connection.Item == nil
        or connection.Item.Removed then
        return
    end

    local sender = signal.sender
    local now = Timer.GetTime()
    if lastTeleportTime[sender] ~= nil
        and now - lastTeleportTime[sender] < TELEPORT_COOLDOWN then
        return
    end

    lastTeleportTime[sender] = now
    TeleportCharacter(sender, connection.Item.WorldPosition)
end

Hook.Add("signalReceived.aic_teleport_gate_a", "AIC.TeleportGateA", HandleTeleportSignal)
Hook.Add("signalReceived.aic_teleport_gate_b", "AIC.TeleportGateB", HandleTeleportSignal)
Hook.Add("signalReceived.aic_bidirectional_teleport_gate", "AIC.BidirectionalTeleportGate", HandleTeleportSignal)

Hook.Add("roundEnd", "AIC.TeleportGates.ClearCooldowns", function()
    lastTeleportTime = {}
end)
