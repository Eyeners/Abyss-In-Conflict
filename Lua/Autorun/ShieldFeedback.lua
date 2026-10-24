-- Cosmetic bridge only. Shield decisions and battery consumption live in AICShield.cs.
local EVENT = "aic_archon_shield_impact_sound"
local sounds = {}
local function PlayArchon(position)
    if not CLIENT then return end
    if #sounds == 0 then
        for _, name in ipairs({ "Shield impact 1.ogg", "Shield impact 1H.ogg", "Shield impact 1L.ogg" }) do
            sounds[#sounds + 1] = Game.SoundManager.LoadSound(ST_Lua.Path .. "/践行者/音效/" .. name)
        end
    end
    SoundPlayer.PlaySound(sounds[math.random(1, #sounds)], position, 1, 2000, 1)
end
if CLIENT and Game.IsMultiplayer then
    Networking.Receive(EVENT, function(message)
        PlayArchon(Vector2(message.ReadDouble(), message.ReadDouble()))
    end)
end
Hook.Add("aic_shield_blocked", "AIC.ShieldFeedback", function(armor, character, kind, broken)
    if Game.IsMultiplayer and not SERVER then return end
    if kind == "bastion" then
        if AIC.BastionShieldSounds then
            AIC.BastionShieldSounds.PlayImpact(armor)
            if broken then AIC.BastionShieldSounds.PlayBreak(armor) end
        end
    elseif kind == "archon" then
        if Game.IsMultiplayer then
            local message = Networking.Start(EVENT)
            message.WriteDouble(armor.WorldPosition.X)
            message.WriteDouble(armor.WorldPosition.Y)
            Networking.Send(message)
        else
            PlayArchon(armor.WorldPosition)
        end
    end
end)
