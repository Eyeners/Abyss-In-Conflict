if SERVER then return end

LuaUserData.MakePropertyAccessible(
    Descriptors["Barotrauma.Items.Components.StatusHUD"],
    "ThermalGoggles"
)
local StatusHUDStatic = LuaUserData.CreateStatic(
    "Barotrauma.Items.Components.StatusHUD"
)

local ARMOR_IDENTIFIER = "aic_foresight_t3_armor"
local GRID_SIZE = 16
local GRID_RANGE = 5000
local CACHE_INTERVAL = 0.3
local NATIVE_THERMAL_LIMB_THRESHOLD = 32
local NATIVE_THERMAL_CELL_AREA_THRESHOLD = 512
local MAX_CANDIDATE_CELLS_PER_LIMB = 4096
local MAX_CACHED_CELLS = 8192
local APPROXIMATE_COVERAGE = 0.90
local MAX_APPROXIMATE_GRID_AREA = 16384
local MAX_APPROXIMATE_GRID_DIMENSION = 128
local MAX_APPROXIMATE_RECTS_PER_COMPONENT = 64
local MAX_APPROXIMATE_BAND_WORK = 131072
local MAX_APPROXIMATE_TOTAL_WORK = 524288
local MIN_BASELINE_RECTS_FOR_APPROXIMATION = 4
local RELATION_NEUTRAL = 1
local RELATION_FRIENDLY = 2
local RELATION_HOSTILE = 3
local GRID_COLORS = {
    [RELATION_NEUTRAL] = Color(235, 245, 255, 80),
    [RELATION_FRIENDLY] = Color(100, 190, 255, 90),
    [RELATION_HOSTILE] = Color(255, 75, 75, 95)
}

local suppressedHud = setmetatable({}, { __mode = "k" })
local cachedRects = {}
local cachedNativeTargets = {}
local cachedNativeRelations = {}
local cachedUser = nil
local cachedRange = nil
local cachedShowDead = nil
local nextCacheUpdate = 0
local drawErrorReported = false
local safetyWarningReported = false

local function IsForesightHud(instance)
    return instance ~= nil
        and instance.item ~= nil
        and instance.item.Prefab ~= nil
        and string.lower(tostring(instance.item.Prefab.Identifier.Value)) == ARMOR_IDENTIFIER
end

local function GetRelation(user, target)
    if user.TeamID ~= CharacterTeamType.None and target.TeamID == user.TeamID then
        return RELATION_FRIENDLY
    end
    if user.IsOnFriendlyTeam(target) then
        return RELATION_NEUTRAL
    end
    return RELATION_HOSTILE
end

local function ShouldUseNativeThermal(target)
    local mainLimb = target.AnimController.MainLimb
    local qualifyingLimbCount = 0
    local projectedCellArea = 0

    for limb in target.AnimController.Limbs do
        if limb == mainLimb or limb.Mass >= 0.5 then
            qualifyingLimbCount = qualifyingLimbCount + 1
            if qualifyingLimbCount >= NATIVE_THERMAL_LIMB_THRESHOLD then
                return true
            end

            local body = limb.body
            if body ~= nil and body.Enabled then
                local size = ConvertUnits.ToDisplayUnits(body.GetSize())
                if size.X > 0 and size.Y > 0 then
                    projectedCellArea = projectedCellArea
                        + size.X * size.Y / (GRID_SIZE * GRID_SIZE)
                    if projectedCellArea >= NATIVE_THERMAL_CELL_AREA_THRESHOLD then
                        return true
                    end
                end
            end
        end
    end

    return false
end

local function GetGridOrigin(hull)
    -- Hull grids follow their submarine; open water falls back to the world grid.
    if hull == nil or hull.Removed then
        return 0, 0, "world"
    end

    local originX = hull.Rect.X
    local originY = hull.Rect.Y
    if hull.Submarine ~= nil then
        originX = originX + hull.Submarine.DrawPosition.X
        originY = originY + hull.Submarine.DrawPosition.Y
    end
    return originX, originY, tostring(hull.ID)
end

local function AddBodyCells(body, hull, relation, weight, cellLookup, cells)
    if body == nil or not body.Enabled then return end

    local size = ConvertUnits.ToDisplayUnits(body.GetSize())
    if size.X <= 0 or size.Y <= 0 then return end

    local center = body.DrawPosition
    local rotation = body.DrawRotation
    local cosRotation = math.cos(rotation)
    local sinRotation = math.sin(rotation)
    local absCos = math.abs(cosRotation)
    local absSin = math.abs(sinRotation)
    local halfWidth = size.X * 0.5
    local halfHeight = size.Y * 0.5

    local extentX = absCos * halfWidth + absSin * halfHeight
    local extentY = absSin * halfWidth + absCos * halfHeight

    local originX, originY, gridKey = GetGridOrigin(hull)
    local minGridX = math.floor((center.X - extentX - originX) / GRID_SIZE)
    local maxGridX = math.floor((center.X + extentX - originX - 0.001) / GRID_SIZE)
    local minGridY = math.floor((center.Y - extentY - originY) / GRID_SIZE)
    local maxGridY = math.floor((center.Y + extentY - originY - 0.001) / GRID_SIZE)
    local candidateCount = (maxGridX - minGridX + 1) * (maxGridY - minGridY + 1)
    if candidateCount > MAX_CANDIDATE_CELLS_PER_LIMB then
        if not safetyWarningReported then
            print(string.format(
                "[AIC] Grid thermal skipped an oversized limb collider (%d candidate cells, %.1fx%.1f).",
                candidateCount,
                size.X,
                size.Y
            ))
            safetyWarningReported = true
        end
        return false
    end

    local cellHalfSize = GRID_SIZE * 0.5
    local cellProjection = cellHalfSize * (absCos + absSin)

    for gridX = minGridX, maxGridX do
        for gridY = minGridY, maxGridY do
            local cellCenterX = originX + (gridX + 0.5) * GRID_SIZE
            local cellCenterY = originY + (gridY + 0.5) * GRID_SIZE
            local deltaX = cellCenterX - center.X
            local deltaY = cellCenterY - center.Y

            -- Transform the cell center into the limb body's local space.
            local localX = cosRotation * deltaX + sinRotation * deltaY
            local localY = -sinRotation * deltaX + cosRotation * deltaY
            if math.abs(localX) <= halfWidth + cellProjection
                and math.abs(localY) <= halfHeight + cellProjection then
                local key = gridKey .. ":" .. gridX .. ":" .. gridY
                local existingCell = cellLookup[key]
                if existingCell == nil then
                    if #cells >= MAX_CACHED_CELLS then
                        if not safetyWarningReported then
                            print(string.format(
                                "[AIC] Grid thermal reached its safety limit of %d cached cells.",
                                MAX_CACHED_CELLS
                            ))
                            safetyWarningReported = true
                        end
                        return true
                    end
                    local cell = {
                        hull = hull,
                        gridX = gridX,
                        gridY = gridY,
                        relation = relation,
                        weight = weight
                    }
                    cellLookup[key] = cell
                    cells[#cells + 1] = cell
                else
                    if relation > existingCell.relation then
                        existingCell.relation = relation
                    end
                    if weight > (existingCell.weight or 0) then
                        existingCell.weight = weight
                    end
                end
            end
        end
    end
    return false
end

local WORLD_GROUP_KEY = {}

local function MergeCells(cells)
    local groups = {}
    local groupLookup = {}

    for _, cell in ipairs(cells) do
        local hullKey = cell.hull or WORLD_GROUP_KEY
        local relationGroups = groupLookup[hullKey]
        if relationGroups == nil then
            relationGroups = {}
            groupLookup[hullKey] = relationGroups
        end

        local group = relationGroups[cell.relation]
        if group == nil then
            group = {
                hull = cell.hull,
                relation = cell.relation,
                rows = {},
                rowYs = {}
            }
            relationGroups[cell.relation] = group
            groups[#groups + 1] = group
        end

        local row = group.rows[cell.gridY]
        if row == nil then
            row = {}
            group.rows[cell.gridY] = row
            group.rowYs[#group.rowYs + 1] = cell.gridY
        end
        row[#row + 1] = cell.gridX
    end

    local mergedRects = {}
    for _, group in ipairs(groups) do
        table.sort(group.rowYs)
        local activeRects = {}

        for _, gridY in ipairs(group.rowYs) do
            local row = group.rows[gridY]
            table.sort(row)
            local nextActiveRects = {}

            local function AddRun(runStart, runEnd)
                local runKey = tostring(runStart) .. ":" .. tostring(runEnd)
                local rect = activeRects[runKey]
                if rect ~= nil and rect.maxGridY == gridY - 1 then
                    rect.maxGridY = gridY
                else
                    rect = {
                        hull = group.hull,
                        relation = group.relation,
                        minGridX = runStart,
                        maxGridX = runEnd,
                        minGridY = gridY,
                        maxGridY = gridY
                    }
                    mergedRects[#mergedRects + 1] = rect
                end
                nextActiveRects[runKey] = rect
            end

            local runStart = row[1]
            local runEnd = runStart
            for index = 2, #row do
                local gridX = row[index]
                if gridX == runEnd + 1 then
                    runEnd = gridX
                else
                    AddRun(runStart, runEnd)
                    runStart = gridX
                    runEnd = gridX
                end
            end
            AddRun(runStart, runEnd)
            activeRects = nextActiveRects
        end
    end

    return mergedRects
end

local function BuildApproximationGroups(cells)
    local groups = {}
    local hullLookup = {}

    for _, cell in ipairs(cells) do
        local hullKey = cell.hull or WORLD_GROUP_KEY
        local hullGroup = hullLookup[hullKey]
        if hullGroup == nil then
            hullGroup = {
                rows = {},
                relationGroups = {}
            }
            hullLookup[hullKey] = hullGroup
        end

        local allRow = hullGroup.rows[cell.gridY]
        if allRow == nil then
            allRow = {}
            hullGroup.rows[cell.gridY] = allRow
        end
        allRow[cell.gridX] = cell

        local group = hullGroup.relationGroups[cell.relation]
        if group == nil then
            group = {
                hull = cell.hull,
                relation = cell.relation,
                cells = {},
                rows = {},
                allRows = hullGroup.rows
            }
            hullGroup.relationGroups[cell.relation] = group
            groups[#groups + 1] = group
        end

        group.cells[#group.cells + 1] = cell
        local row = group.rows[cell.gridY]
        if row == nil then
            row = {}
            group.rows[cell.gridY] = row
        end
        row[cell.gridX] = cell
    end

    return groups
end

local function BuildConnectedComponents(group)
    local components = {}
    local visited = {}

    for _, startCell in ipairs(group.cells) do
        if not visited[startCell] then
            local component = {}
            local queue = { startCell }
            local queueIndex = 1
            visited[startCell] = true

            while queueIndex <= #queue do
                local cell = queue[queueIndex]
                queueIndex = queueIndex + 1
                component[#component + 1] = cell

                for offsetY = -1, 1 do
                    local row = group.rows[cell.gridY + offsetY]
                    if row ~= nil then
                        for offsetX = -1, 1 do
                            if offsetX ~= 0 or offsetY ~= 0 then
                                local neighbor = row[cell.gridX + offsetX]
                                if neighbor ~= nil and not visited[neighbor] then
                                    visited[neighbor] = true
                                    queue[#queue + 1] = neighbor
                                end
                            end
                        end
                    end
                end
            end

            components[#components + 1] = component
        end
    end

    return components
end


local function BuildApproximationGrid(component, allRows)
    local minGridX, maxGridX = component[1].gridX, component[1].gridX
    local minGridY, maxGridY = component[1].gridY, component[1].gridY
    for index = 2, #component do
        local cell = component[index]
        minGridX = math.min(minGridX, cell.gridX)
        maxGridX = math.max(maxGridX, cell.gridX)
        minGridY = math.min(minGridY, cell.gridY)
        maxGridY = math.max(maxGridY, cell.gridY)
    end

    local width = maxGridX - minGridX + 1
    local height = maxGridY - minGridY + 1
    local shorterSide = math.min(width, height)
    local longerSide = math.max(width, height)
    if width > MAX_APPROXIMATE_GRID_DIMENSION
        or height > MAX_APPROXIMATE_GRID_DIMENSION
        or width * height > MAX_APPROXIMATE_GRID_AREA
        or shorterSide * shorterSide * longerSide > MAX_APPROXIMATE_BAND_WORK then
        return nil
    end

    local occupied = {}
    local rawWeights = {}
    local forbidden = {}
    local outside = {}
    local active = {}
    local weights = {}
    for y = 1, height do
        occupied[y] = {}
        rawWeights[y] = {}
        forbidden[y] = {}
        outside[y] = {}
        active[y] = {}
        weights[y] = {}
    end

    for _, cell in ipairs(component) do
        local x = cell.gridX - minGridX + 1
        local y = cell.gridY - minGridY + 1
        occupied[y][x] = true
        rawWeights[y][x] = math.max(rawWeights[y][x] or 0, cell.weight or 1)
    end

    -- Cells belonging to another component or color may not be filled as holes.
    for y = 1, height do
        local allRow = allRows[minGridY + y - 1]
        if allRow ~= nil then
            for x = 1, width do
                if not occupied[y][x] and allRow[minGridX + x - 1] ~= nil then
                    forbidden[y][x] = true
                end
            end
        end
    end

    local queueX = {}
    local queueY = {}
    local queueHead = 1
    local queueTail = 0
    local function EnqueueOutside(x, y)
        if x < 1 or x > width or y < 1 or y > height then return end
        if occupied[y][x] or outside[y][x] then return end
        outside[y][x] = true
        queueTail = queueTail + 1
        queueX[queueTail] = x
        queueY[queueTail] = y
    end

    for x = 1, width do
        EnqueueOutside(x, 1)
        EnqueueOutside(x, height)
    end
    for y = 1, height do
        EnqueueOutside(1, y)
        EnqueueOutside(width, y)
    end

    while queueHead <= queueTail do
        local x = queueX[queueHead]
        local y = queueY[queueHead]
        queueHead = queueHead + 1
        EnqueueOutside(x - 1, y)
        EnqueueOutside(x + 1, y)
        EnqueueOutside(x, y - 1)
        EnqueueOutside(x, y + 1)
    end

    local totalWeight = 0
    for y = 1, height do
        for x = 1, width do
            if occupied[y][x] then
                local neighborCount = 0
                if x > 1 and occupied[y][x - 1] then neighborCount = neighborCount + 1 end
                if x < width and occupied[y][x + 1] then neighborCount = neighborCount + 1 end
                if y > 1 and occupied[y - 1][x] then neighborCount = neighborCount + 1 end
                if y < height and occupied[y + 1][x] then neighborCount = neighborCount + 1 end

                local weight = rawWeights[y][x] or 1
                if neighborCount < 4 then
                    weight = math.max(1, weight - 1)
                end
                active[y][x] = true
                weights[y][x] = weight
                totalWeight = totalWeight + weight
            elseif not outside[y][x] and not forbidden[y][x] then
                -- Enclosed holes may be painted, but do not count toward coverage.
                active[y][x] = true
                weights[y][x] = 0
            end
        end
    end

    return {
        width = width,
        height = height,
        minGridX = minGridX,
        minGridY = minGridY,
        active = active,
        weights = weights,
        totalWeight = totalWeight,
        bandWork = shorterSide * shorterSide * longerSide
    }
end

local function FindBestWeightedRectangle(grid)
    local width = grid.width
    local height = grid.height
    local active = grid.active
    local weights = grid.weights
    local best = nil
    local bestWeight = 0
    local bestArea = 0

    -- Squaring the shorter axis keeps the exhaustive band search inexpensive.
    if height <= width then
        for top = 1, height do
            local columnActive = {}
            local columnWeights = {}
            for x = 1, width do
                columnActive[x] = true
                columnWeights[x] = 0
            end

            for bottom = top, height do
                local activeRow = active[bottom]
                local weightRow = weights[bottom]
                for x = 1, width do
                    if columnActive[x] and activeRow[x] then
                        columnWeights[x] = columnWeights[x] + (weightRow[x] or 0)
                    else
                        columnActive[x] = false
                        columnWeights[x] = 0
                    end
                end

                local x = 1
                while x <= width do
                    if not columnActive[x] then
                        x = x + 1
                    else
                        local runStart = x
                        local runWeight = 0
                        while x <= width and columnActive[x] do
                            runWeight = runWeight + columnWeights[x]
                            x = x + 1
                        end
                        local runEnd = x - 1
                        local area = (bottom - top + 1) * (runEnd - runStart + 1)
                        if runWeight > 0
                            and (runWeight > bestWeight
                                or (runWeight == bestWeight and area > bestArea)) then
                            bestWeight = runWeight
                            bestArea = area
                            best = {
                                minX = runStart,
                                maxX = runEnd,
                                minY = top,
                                maxY = bottom
                            }
                        end
                    end
                end
            end
        end
    else
        for left = 1, width do
            local rowActive = {}
            local rowWeights = {}
            for y = 1, height do
                rowActive[y] = true
                rowWeights[y] = 0
            end

            for right = left, width do
                for y = 1, height do
                    if rowActive[y] and active[y][right] then
                        rowWeights[y] = rowWeights[y] + (weights[y][right] or 0)
                    else
                        rowActive[y] = false
                        rowWeights[y] = 0
                    end
                end

                local y = 1
                while y <= height do
                    if not rowActive[y] then
                        y = y + 1
                    else
                        local runStart = y
                        local runWeight = 0
                        while y <= height and rowActive[y] do
                            runWeight = runWeight + rowWeights[y]
                            y = y + 1
                        end
                        local runEnd = y - 1
                        local area = (right - left + 1) * (runEnd - runStart + 1)
                        if runWeight > 0
                            and (runWeight > bestWeight
                                or (runWeight == bestWeight and area > bestArea)) then
                            bestWeight = runWeight
                            bestArea = area
                            best = {
                                minX = left,
                                maxX = right,
                                minY = runStart,
                                maxY = runEnd
                            }
                        end
                    end
                end
            end
        end
    end

    return best
end

local function RemoveRectangle(grid, rect)
    local removedWeight = 0
    for y = rect.minY, rect.maxY do
        for x = rect.minX, rect.maxX do
            if grid.active[y][x] then
                removedWeight = removedWeight + (grid.weights[y][x] or 0)
                grid.active[y][x] = false
                grid.weights[y][x] = 0
            end
        end
    end
    return removedWeight
end

local function ApproximateComponent(component, allRows)
    local baselineRects = MergeCells(component)
    if #baselineRects < MIN_BASELINE_RECTS_FOR_APPROXIMATION then return baselineRects end

    local grid = BuildApproximationGrid(component, allRows)
    if grid == nil or grid.totalWeight <= 0 then return baselineRects end
    local maximumUsefulIterations = math.min(
        #baselineRects - 1,
        MAX_APPROXIMATE_RECTS_PER_COMPONENT
    )
    if grid.bandWork * maximumUsefulIterations > MAX_APPROXIMATE_TOTAL_WORK then
        return baselineRects
    end

    local allowedLoss = math.floor(grid.totalWeight * (1 - APPROXIMATE_COVERAGE) + 0.0001)
    local remainingWeight = grid.totalWeight
    local approximateRects = {}

    while remainingWeight > allowedLoss
        and #approximateRects < MAX_APPROXIMATE_RECTS_PER_COMPONENT do
        local rect = FindBestWeightedRectangle(grid)
        if rect == nil then break end

        local removedWeight = RemoveRectangle(grid, rect)
        if removedWeight <= 0 then break end
        remainingWeight = remainingWeight - removedWeight
        approximateRects[#approximateRects + 1] = {
            hull = component[1].hull,
            relation = component[1].relation,
            minGridX = grid.minGridX + rect.minX - 1,
            maxGridX = grid.minGridX + rect.maxX - 1,
            minGridY = grid.minGridY + rect.minY - 1,
            maxGridY = grid.minGridY + rect.maxY - 1
        }

        -- Approximation is only useful when it reduces per-frame draw calls.
        if #approximateRects >= #baselineRects then return baselineRects end
    end

    if remainingWeight > allowedLoss then return baselineRects end
    return approximateRects
end

local function ApproximateCells(cells)
    local rectangles = {}
    for _, group in ipairs(BuildApproximationGroups(cells)) do
        for _, component in ipairs(BuildConnectedComponents(group)) do
            local componentRects = ApproximateComponent(component, group.allRows)
            for _, rect in ipairs(componentRects) do
                rectangles[#rectangles + 1] = rect
            end
        end
    end
    return rectangles
end

local function RebuildCache(user, range, showDeadCharacters)
    local cells = {}
    local cellLookup = {}
    local nativeTargets = {}
    local nativeRelations = {}
    local userPosition = user.WorldPosition
    local rangeSquared = range * range
    local capacityReached = false

    for target in Character.CharacterList do
        -- This grid mode reads collision bodies directly, intentionally bypassing
        -- CharacterParams.HideInThermalGoggles used by the native thermal HUD.
        if target ~= user
            and target.Enabled
            and not target.Removed
            and (showDeadCharacters or not target.IsDead)
            and target.AnimController ~= nil then
            local targetPosition = target.WorldPosition
            local deltaX = targetPosition.X - userPosition.X
            local deltaY = targetPosition.Y - userPosition.Y
            if deltaX * deltaX + deltaY * deltaY <= rangeSquared then
                local mainLimb = target.AnimController.MainLimb
                local hull = target.CurrentHull
                local relation = GetRelation(user, target)
                if ShouldUseNativeThermal(target) then
                    nativeTargets[target.ID] = relation
                    nativeRelations[relation] = true
                else
                    for limb in target.AnimController.Limbs do
                        if limb == mainLimb or limb.Mass >= 0.5 then
                            local weight = limb == mainLimb and 3 or 2
                            if AddBodyCells(limb.body, hull, relation, weight, cellLookup, cells) then
                                capacityReached = true
                                break
                            end
                        end
                    end
                end
                if capacityReached then break end
            end
        end
        if capacityReached then break end
    end

    cachedRects = ApproximateCells(cells)
    cachedNativeTargets = nativeTargets
    cachedNativeRelations = nativeRelations

    cachedUser = user
    cachedRange = range
    cachedShowDead = showDeadCharacters
    nextCacheUpdate = Timer.Time + CACHE_INTERVAL
end

local function DrawNativeRelation(spriteBatch, user, range, showDeadCharacters, relation)
    local originalVisibility = {}
    local succeeded, errorMessage = pcall(function()
        for target in Character.CharacterList do
            if target.Params ~= nil then
                originalVisibility[#originalVisibility + 1] = {
                    params = target.Params,
                    hidden = target.Params.HideInThermalGoggles
                }
                target.Params.HideInThermalGoggles = cachedNativeTargets[target.ID] ~= relation
            end
        end

        StatusHUDStatic.DrawThermalOverlay(
            spriteBatch,
            user,
            user,
            GRID_COLORS[relation] or GRID_COLORS[RELATION_NEUTRAL],
            range,
            Timer.Time % 10000,
            showDeadCharacters
        )
    end)

    for _, entry in ipairs(originalVisibility) do
        entry.params.HideInThermalGoggles = entry.hidden
    end

    if not succeeded then
        error(errorMessage)
    end
end

local function DrawNativeTargets(spriteBatch, user, range, showDeadCharacters)
    for relation = RELATION_NEUTRAL, RELATION_HOSTILE do
        if cachedNativeRelations[relation] then
            DrawNativeRelation(
                spriteBatch,
                user,
                range,
                showDeadCharacters,
                relation
            )
        end
    end
end

local function RoundScreenPixel(value)
    return math.floor(value + 0.5)
end

local function DrawCachedRects(spriteBatch)
    local screen = Screen.Selected
    if screen == nil or screen.Cam == nil then return end

    local camera = screen.Cam
    local worldView = camera.WorldView
    local viewLeft = worldView.X
    local viewRight = worldView.Right
    local viewTop = worldView.Y
    local viewBottom = worldView.Y - worldView.Height
    local hullOrigins = {}

    for _, rect in ipairs(cachedRects) do
        local originX, originY = 0, 0
        if rect.hull ~= nil then
            local origin = hullOrigins[rect.hull]
            if origin == nil then
                originX, originY = GetGridOrigin(rect.hull)
                origin = { x = originX, y = originY }
                hullOrigins[rect.hull] = origin
            else
                originX, originY = origin.x, origin.y
            end
        end
        local worldLeft = originX + rect.minGridX * GRID_SIZE
        local worldBottom = originY + rect.minGridY * GRID_SIZE
        local worldRight = originX + (rect.maxGridX + 1) * GRID_SIZE
        local worldTop = originY + (rect.maxGridY + 1) * GRID_SIZE

        if worldRight >= viewLeft and worldLeft <= viewRight
            and worldTop >= viewBottom and worldBottom <= viewTop then
            local cornerA = camera.WorldToScreen(Vector2(worldLeft, worldTop))
            local cornerB = camera.WorldToScreen(Vector2(worldRight, worldBottom))
            -- Shared edges must use the same rounding rule. Mixing floor and ceil
            -- overlaps translucent rectangles by one pixel and creates bright seams.
            local left = RoundScreenPixel(math.min(cornerA.X, cornerB.X))
            local top = RoundScreenPixel(math.min(cornerA.Y, cornerB.Y))
            local right = RoundScreenPixel(math.max(cornerA.X, cornerB.X))
            local bottom = RoundScreenPixel(math.max(cornerA.Y, cornerB.Y))

            local width = right - left
            local height = bottom - top
            GUI.DrawRectangle(
                spriteBatch,
                Rectangle(
                    left,
                    top,
                    math.max(width, 1),
                    math.max(height, 1)
                ),
                GRID_COLORS[rect.relation] or GRID_COLORS[RELATION_NEUTRAL],
                true,
                0
            )
        end
    end
end

local drawHudParameters = {
    "Microsoft.Xna.Framework.Graphics.SpriteBatch",
    "Barotrauma.Character"
}

Hook.Patch(
    "aic_foresight_grid_thermal_suppress",
    "Barotrauma.Items.Components.StatusHUD",
    "DrawHUD",
    drawHudParameters,
    function(instance, ptable)
        if IsForesightHud(instance) and instance.ThermalGoggles then
            local user = ptable["character"]
            local spriteBatch = ptable["spriteBatch"]
            if user ~= nil and spriteBatch ~= nil then
                local succeeded, errorMessage = pcall(function()
                    local range = GRID_RANGE
                    local showDeadCharacters = instance.ShowDeadCharacters
                    if cachedUser ~= user
                        or cachedRange ~= range
                        or cachedShowDead ~= showDeadCharacters
                        or Timer.Time >= nextCacheUpdate then
                        RebuildCache(user, range, showDeadCharacters)
                    end
                    DrawCachedRects(spriteBatch)
                    DrawNativeTargets(spriteBatch, user, range, showDeadCharacters)
                end)
                if not succeeded then
                    if not drawErrorReported then
                        print("[AIC] Grid thermal draw failed: " .. tostring(errorMessage))
                        drawErrorReported = true
                    end
                    return
                end
                drawErrorReported = false
            end

            suppressedHud[instance] = true
            instance.ThermalGoggles = false
        end
    end,
    Hook.HookMethodType.Before
)

Hook.Patch(
    "aic_foresight_grid_thermal_draw",
    "Barotrauma.Items.Components.StatusHUD",
    "DrawHUD",
    drawHudParameters,
    function(instance, ptable)
        if not suppressedHud[instance] then return end

        suppressedHud[instance] = nil
        instance.ThermalGoggles = true
    end,
    Hook.HookMethodType.After
)

Hook.Add("roundEnd", "aic_foresight_grid_thermal_clear", function()
    cachedRects = {}
    cachedNativeTargets = {}
    cachedNativeRelations = {}
    cachedUser = nil
    nextCacheUpdate = 0
    drawErrorReported = false
    safetyWarningReported = false
end)
