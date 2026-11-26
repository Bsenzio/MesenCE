-- RetroArcDo HD Studio v0.6 - deterministic menu-escape input.
--
-- Same contract as deterministic_input.lua (emulator frame counter, no screen
-- reads), but keeps issuing Start/A/Down taps after the initial boot window.
-- Used for games whose deterministic test run is still on a title/status/pause
-- screen when the first capture frame is reached. The exact same script is used
-- for capture and verification, so HD and original passes remain comparable.

local function inputForFrame(frame)
  local input = {
    a = false, b = false, select = false, start = false,
    up = false, down = false, left = false, right = false
  }

  if frame < 900 then
    if frame % 60 < 5 then input.start = true end
    if frame % 90 >= 40 and frame % 90 < 45 then input.a = true end
    if frame % 240 >= 180 and frame % 240 < 185 then input.down = true end
    if frame % 300 >= 250 and frame % 300 < 256 then input.right = true end
  else
    local t = frame - 900
    input.right = true
    input.b = true
    if t % 48 < 9 then input.a = true end
    -- Persistent menu escape: short taps, separated enough that a pause caused
    -- by a Start tap is undone by the next one.
    if t % 300 < 4 then
      input.start = true
    elseif t % 300 >= 50 and t % 300 < 54 then
      input.a = true
    elseif t % 300 >= 100 and t % 300 < 104 then
      input.down = true
    elseif t % 300 >= 150 and t % 300 < 154 then
      input.a = true
    end
  end
  return input
end

local function onInput()
  local state = emu.getState()
  local frame = state and state.frameCount or 0
  emu.setInput(inputForFrame(frame), 0)
end

emu.addEventCallback(onInput, emu.eventType.inputPolled)
emu.log("Deterministic menu-escape input ready")
