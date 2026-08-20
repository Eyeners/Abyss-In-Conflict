if CLIENT and Game.IsMultiplayer then return end

local DRAG_STUN_IDENTIFIER = "aic_dragstun"

local function ApplyDragStun(character, afflictions, damageMultiplier)
    if character == nil or character.CharacterHealth == nil or afflictions == nil then
        return
    end

    local attackMultiplier = tonumber(damageMultiplier) or 1
    local vitalityScale = 100 / math.max(character.MaxVitality or 100, 0.001)

    for affliction in afflictions do
        local prefab = affliction.Prefab
        if prefab ~= nil and prefab.Identifier ~= nil then
            local identifier = string.lower(tostring(prefab.Identifier.Value))
            if identifier == DRAG_STUN_IDENTIFIER then
                local strength = math.max(affliction.Strength or 0, 0)
                if prefab.AffectedByAttackMultipliers then
                    strength = strength * attackMultiplier
                end
                strength = strength * vitalityScale

                -- Consume the marker before normal affliction resistance is calculated.
                affliction.Strength = 0

                if strength > 0 then
                    local currentStun = character.CharacterHealth.Stun or 0
                    character.SetStun(currentStun + strength, true, false)
                end
            end
        end
    end
end

Hook.Add("character.damageLimb", "AIC_DragStunDamageLimb", function(character, worldPosition, hitLimb, afflictions, stun, playSound, attackImpulse, attacker, damageMultiplier, allowStacking, penetration, shouldImplode)
    ApplyDragStun(character, afflictions, damageMultiplier)
end)
