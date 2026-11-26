-- RetroArcDo HD Studio v0.6 - deterministic, resolution-independent test input.
--
-- Used by the automated replacement benchmark for BOTH the live capture and the
-- HD/original verification pass. It never reads the screen, so enabling an HD pack
-- cannot change the controller input. The emulator frame counter is used instead of
-- a script-local counter, which keeps capture and verification aligned even when a
-- script is attached a few frames after power-on.
--
-- The sequence is intentionally generic: clear logos and "PRESS START" screens,
-- then walk right, run and jump. It is a test driver, not a game-specific solver.

local function inputForFrame(frame)
  local input = {
    a = false, b = false, select = false, start = false,
    up = false, down = false, left = false, right = false
  }

  if frame < 900 then
    -- Title/menu progression: alternating Start and A taps, occasional Down/Right.
    if frame % 90 < 6 then input.start = true end
    if frame % 150 >= 70 and frame % 150 < 76 then input.a = true end
    if frame % 240 >= 200 and frame % 240 < 206 then input.down = true end
    if frame % 300 >= 260 and frame % 300 < 266 then input.right = true end
    if frame % 420 >= 380 and frame % 420 < 386 then input.b = true end
  else
    local t = frame - 900
    -- Walk right and run; jump in a steady cadence, attack with B.
    input.right = true
    input.b = true
    if t % 48 < 9 then input.a = true end
    if t % 640 >= 600 and t % 640 < 630 then input.left = true end
    if t % 640 >= 630 and t % 640 < 650 then input.up = true end
    -- A short neutral gap lets menus opened by B (if any) be dismissed with Start
    -- only much later, so gameplay is not paused accidentally.
    if t > 3000 and t % 3000 >= 2990 then input.start = true end
  end
  return input
end

local function onInput()
  local state = emu.getState()
  local frame = state and state.frameCount or 0
  emu.setInput(inputForFrame(frame), 0)
end

emu.addEventCallback(onInput, emu.eventType.inputPolled)
emu.log("Deterministic test input ready")
