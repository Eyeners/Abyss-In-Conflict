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
