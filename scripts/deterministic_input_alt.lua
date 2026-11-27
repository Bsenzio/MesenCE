-- RetroArcDo HD Studio v0.8 - alternative deterministic exploration input.
--
-- The default driver (deterministic_input.lua) clears logos and then walks
-- right. The menu driver keeps issuing Start/A taps. This third profile uses a
-- different deterministic cadence for titles that ignore the first two: longer
-- directional menus, more Start presses and a different gameplay rhythm.
--
-- It never reads the screen, so the HD and original passes receive identical
-- controller input. Only the emulator frame counter is used.

local function inputForFrame(frame)
  local input = {
    a = false, b = false, select = false, start = false,
    up = false, down = false, left = false, right = false
  }

  if frame < 1800 then
    -- Slow, deliberate menu traversal: Start, then Down/Right/A cycles.
    if frame % 120 < 6 then input.start = true end
    if frame % 120 >= 30 and frame % 120 < 36 then input.down = true end
    if frame % 120 >= 55 and frame % 120 < 61 then input.a = true end
    if frame % 120 >= 80 and frame % 120 < 86 then input.right = true end
    if frame % 120 >= 100 and frame % 120 < 106 then input.a = true end
  else
    local t = frame - 1800
    -- Gameplay rhythm deliberately offset from the default driver.
    input.right = true
    if t % 90 < 7 then input.a = true end
    if t % 90 >= 30 and t % 90 < 38 then input.b = true end
    if t % 512 >= 430 and t % 512 < 448 then input.left = true end
    if t % 512 >= 448 and t % 512 < 462 then input.up = true end
    if t % 512 >= 462 and t % 512 < 476 then input.down = true end
    -- Periodic menu escape for pause/status screens opened by Start.
    if t > 2400 and t % 2400 >= 2390 then input.start = true end
  end
  return input
end

local function onInput()
  local state = emu.getState()
  local frame = state and state.frameCount or 0
  emu.setInput(inputForFrame(frame), 0)
end

emu.addEventCallback(onInput, emu.eventType.inputPolled)
emu.log("Alternative deterministic test input ready")
