-- Deterministic preview input for --verify.
--
-- Unlike explore.lua it never samples the screen, so HD and original passes receive
-- exactly the same controller input even though one renders at 4x resolution.
-- It taps Start to leave the title/menu, then walks right and jumps periodically.
local frame = 0

local function onInput()
  frame = frame + 1
  local input = {
    a = false, b = false, select = false, start = false,
    up = false, down = false, left = false, right = false
  }

  -- Start taps at a fixed cadence; this leaves most title screens and menus.
  if frame >= 120 and frame < 600 and (frame % 120) < 5 then
    input.start = true
  end

  if frame >= 600 then
    input.right = true
    if frame % 48 < 10 then
      input.a = true
    end
    if frame % 180 > 140 then
      input.b = true
    end
  end

  emu.setInput(input, 0)
end

emu.addEventCallback(onInput, emu.eventType.inputPolled)
